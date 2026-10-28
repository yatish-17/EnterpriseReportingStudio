import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import './DatasetsPage.css';

interface DataSource {
  dataSourceId: number;
  name: string;
  serverAddress: string;
  databaseName: string;
}

interface Dataset {
  datasetId: number;
  dataSourceId: number;
  name: string;
  description: string | null;
  datasetType: string;
  createdAt: string;
  updatedAt: string | null;
}

interface DatasetDetail {
  dataset: Dataset;
  queryDefinition: string;
}

interface DeleteImpact {
  datasetId: number;
  resourceName: string;
  datasetNames: string[];
  reportNames: string[];
}

interface Column {
  name: string;
  dataType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
}

interface Preview {
  columns: Column[];
  rows: (string | number | boolean | null)[][];
  truncated: boolean;
  durationMilliseconds: number;
}

interface SqlObject {
  schema: string;
  name: string;
  type: string;
}

type ParameterType = 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Date' | 'DateTime';

interface ParameterInput {
  dataType: ParameterType;
  value: string;
}

function getQueryParameterNames(query: string) {
  const sql = query
    .replace(/'(?:''|[^'])*'/g, ' ')
    .replace(/--[^\r\n]*|\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\[[^\]]*\]|"(?:""|[^"])*"/g, ' ');
  const names = new Set<string>();
  const pattern = /(^|[^@])@([A-Za-z_][A-Za-z0-9_]*)/g;
  let match = pattern.exec(sql);
  while (match) {
    names.add(match[2]);
    match = pattern.exec(sql);
  }
  return [...names];
}

async function errorMessage(response: Response) {
  try {
    const value: unknown = await response.json();
    if (value && typeof value === 'object' && 'title' in value && typeof value.title === 'string') {
      return value.title;
    }
  } catch {
    // Non-problem responses are represented by their status instead.
  }
  return `Request failed (${response.status}).`;
}

export function DatasetsPage({
  onDesignDataset,
}: {
  onDesignDataset: (datasetId: number) => void;
}) {
  const { accessToken, signOut } = useAuth();
  const [sources, setSources] = useState<DataSource[]>([]);
  const [datasets, setDatasets] = useState<Dataset[]>([]);
  const [editingDatasetId, setEditingDatasetId] = useState<number | null>(null);
  const [dataSourceId, setDataSourceId] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [query, setQuery] = useState('');
  const [parameterInputs, setParameterInputs] = useState<Record<string, ParameterInput>>({});
  const [schemas, setSchemas] = useState<string[]>([]);
  const [schema, setSchema] = useState('');
  const [objects, setObjects] = useState<SqlObject[]>([]);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [deleteImpact, setDeleteImpact] = useState<DeleteImpact | null>(null);
  const [deleting, setDeleting] = useState(false);

  const request = useCallback(async <T,>(url: string, init?: RequestInit): Promise<T> => {
    if (!accessToken) throw new Error('Your session has expired. Sign in again.');
    const response = await fetch(url, {
      ...init,
      headers: {
        Authorization: `Bearer ${accessToken}`,
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });
    if (response.status === 401) {
      await signOut();
      throw new Error('Your session has expired. Sign in again.');
    }
    if (!response.ok) throw new Error(await errorMessage(response));
    if (response.status === 204) return undefined as T;
    return response.json() as Promise<T>;
  }, [accessToken, signOut]);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [sourceRows, datasetRows] = await Promise.all([
        request<DataSource[]>('/api/data-sources'),
        request<Dataset[]>('/api/datasets'),
      ]);
      setSources(sourceRows);
      setDatasets(datasetRows);
      setDataSourceId((current) => current || String(sourceRows[0]?.dataSourceId ?? ''));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Datasets could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [request]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!dataSourceId) {
      setSchemas([]);
      setObjects([]);
      return;
    }
    let active = true;
    void request<{ name: string }[]>(`/api/data-sources/${dataSourceId}/schemas`)
      .then((rows) => {
        if (!active) return;
        setSchemas(rows.map((row) => row.name));
      })
      .catch((cause: unknown) => {
        if (active) setError(cause instanceof Error ? cause.message : 'Schemas could not be loaded.');
      });
    return () => { active = false; };
  }, [dataSourceId, request]);

  async function selectSchema(value: string) {
    setSchema(value);
    setObjects([]);
    setPreview(null);
    if (!value || !dataSourceId) return;
    try {
      const rows = await request<SqlObject[]>(
        `/api/data-sources/${dataSourceId}/schemas/${encodeURIComponent(value)}/objects`,
      );
      setObjects(rows);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Objects could not be loaded.');
    }
  }

  function selectObject(object: SqlObject) {
    const quote = (identifier: string) => `[${identifier.replace(/\]/g, ']]')}]`;
    changeQuery(`SELECT TOP (100) *\nFROM ${quote(object.schema)}.${quote(object.name)}`);
    setPreview(null);
  }

  function parameterBindings() {
    return getQueryParameterNames(query).map((name) => {
      const parameter = parameterInputs[name] ?? { dataType: 'String' as const, value: '' };
      let value: string | number | boolean | null = parameter.value || null;
      if (parameter.value && (parameter.dataType === 'Integer' || parameter.dataType === 'Decimal')) {
        value = Number(parameter.value);
      } else if (parameter.value && parameter.dataType === 'Boolean') {
        value = parameter.value === 'true';
      }
      return { name, dataType: parameter.dataType, value };
    });
  }

  async function previewQuery(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setWorking(true);
    setError(null);
    setPreview(null);
    try {
      const result = await request<Preview>('/api/datasets/preview', {
        method: 'POST',
        body: JSON.stringify({
          dataSourceId: Number(dataSourceId),
          queryDefinition: query,
          parameters: parameterBindings(),
        }),
      });
      setPreview(result);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The query preview failed.');
    } finally {
      setWorking(false);
    }
  }

  async function saveDataset() {
    setWorking(true);
    setError(null);
    try {
      await request<Dataset>(
        editingDatasetId ? `/api/datasets/${editingDatasetId}` : '/api/datasets',
        {
        method: editingDatasetId ? 'PUT' : 'POST',
        body: JSON.stringify({
          dataSourceId: Number(dataSourceId),
          name,
          description: description || null,
          queryDefinition: query,
          parameters: parameterBindings(),
        }),
      });
      setName('');
      setDescription('');
      setQuery('');
      setPreview(null);
      setEditingDatasetId(null);
      setParameterInputs({});
      await load();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The dataset could not be saved.');
    } finally {
      setWorking(false);
    }
  }

  async function editDataset(datasetId: number) {
    setWorking(true);
    setError(null);
    try {
      const detail = await request<DatasetDetail>(`/api/datasets/${datasetId}`);
      setEditingDatasetId(datasetId);
      setDataSourceId(String(detail.dataset.dataSourceId));
      setName(detail.dataset.name);
      setDescription(detail.dataset.description ?? '');
      setQuery(detail.queryDefinition);
      setParameterInputs(Object.fromEntries(
        getQueryParameterNames(detail.queryDefinition).map((parameterName) => [
          parameterName,
          { dataType: 'String' as const, value: '' },
        ]),
      ));
      setPreview(null);
      window.scrollTo({ top: 0, behavior: 'smooth' });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The dataset could not be opened for editing.');
    } finally {
      setWorking(false);
    }
  }

  async function prepareDelete(dataset: Dataset) {
    setError(null);
    setWorking(true);
    try {
      const impact = await request<Omit<DeleteImpact, 'datasetId'>>(
        `/api/datasets/${dataset.datasetId}/delete-impact`,
      );
      setDeleteImpact({ ...impact, datasetId: dataset.datasetId });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The dataset impact could not be loaded.');
    } finally {
      setWorking(false);
    }
  }

  async function deleteDataset() {
    if (!deleteImpact) return;
    if (!datasets.some((item) => item.datasetId === deleteImpact.datasetId)) {
      setDeleteImpact(null);
      setError('This dataset is no longer available. Refresh the list and try again.');
      return;
    }
    setDeleting(true);
    setError(null);
    try {
      await request<void>(`/api/datasets/${deleteImpact.datasetId}`, { method: 'DELETE' });
      setDeleteImpact(null);
      if (editingDatasetId === deleteImpact.datasetId) cancelDatasetEdit();
      await load();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The dataset could not be deleted.');
    } finally {
      setDeleting(false);
    }
  }

  function cancelDatasetEdit() {
    setEditingDatasetId(null);
    setName('');
    setDescription('');
    setQuery('');
    setParameterInputs({});
    setPreview(null);
  }

  function changeQuery(value: string) {
    setQuery(value);
    setPreview(null);
    const existingNames = new Set(getQueryParameterNames(value));
    setParameterInputs((current) => Object.fromEntries(
      Object.entries(current).filter(([parameterName]) => existingNames.has(parameterName)),
    ));
  }

  const queryParameters = getQueryParameterNames(query);

  return (
    <section className="datasets-page" aria-labelledby="datasets-title">
      <div className="datasets-heading">
        <div>
          <span className="login-kicker">DATA PREPARATION</span>
          <h1 id="datasets-title">Datasets</h1>
          <p>Define reusable read-only queries against connections you can access.</p>
        </div>
        <button className="data-source-refresh" type="button" onClick={() => void load()} disabled={loading}>
          <Icon name="activity" size={16} /> Refresh
        </button>
      </div>

      {error && (
        <div className="data-source-alert" role="alert">
          <Icon name="x" size={17} /><span>{error}</span>
          <button type="button" onClick={() => setError(null)} aria-label="Dismiss error"><Icon name="x" size={16} /></button>
        </div>
      )}

      {loading ? (
        <div className="data-source-empty"><span className="spinner spinner--large" /><p>Loading datasets and connections…</p></div>
      ) : sources.length === 0 ? (
        <div className="data-source-empty">
          <span className="data-source-empty-icon"><Icon name="database" size={22} /></span>
          <h3>No accessible SQL Server connections</h3>
          <p>Create a connection or ask an administrator to grant you access before preparing a dataset.</p>
        </div>
      ) : (
        <>
          <form className="dataset-builder" onSubmit={previewQuery}>
            <div className="dataset-builder-heading">
              <div>
                <span className="panel-overline">{editingDatasetId ? 'EDIT DATASET' : 'QUERY DATASET'}</span>
                <h2>{editingDatasetId ? 'Update dataset' : 'Build a dataset'}</h2>
              </div>
              <span className="dataset-readonly-badge"><Icon name="shield" size={14} /> SELECT-only preview</span>
            </div>

            <div className="dataset-builder-grid">
              <div className="dataset-source-browser">
                <label className="data-source-field">
                  <span>Data source</span>
                  <select value={dataSourceId} onChange={(event) => { setDataSourceId(event.target.value); setSchema(''); setObjects([]); setPreview(null); }}>
                    {sources.map((source) => (
                      <option key={source.dataSourceId} value={source.dataSourceId}>
                        {source.name} — {source.databaseName}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="data-source-field">
                  <span>Schema</span>
                  <select value={schema} onChange={(event) => void selectSchema(event.target.value)}>
                    <option value="">Choose a schema</option>
                    {schemas.map((item) => <option key={item} value={item}>{item}</option>)}
                  </select>
                </label>
                <div className="dataset-object-picker">
                  <span className="dataset-picker-label">TABLES & VIEWS</span>
                  {objects.map((object) => (
                    <button type="button" key={`${object.schema}.${object.name}`} onClick={() => selectObject(object)}>
                      <Icon name={object.type === 'View' ? 'eye' : 'database'} size={15} />
                      <span>{object.name}</span><small>{object.type}</small>
                    </button>
                  ))}
                  {schema && objects.length === 0 && <p>No readable objects in this schema.</p>}
                </div>
              </div>

              <div className="dataset-query-editor">
                <label htmlFor="dataset-query">SQL query</label>
                <textarea
                  id="dataset-query"
                  spellCheck={false}
                  autoCapitalize="off"
                  autoCorrect="off"
                  maxLength={32000}
                  placeholder={'SELECT TOP (100) CustomerName, Total\nFROM dbo.CustomerTotals\nORDER BY Total DESC'}
                  value={query}
                  onChange={(event) => changeQuery(event.target.value)}
                  required
                />
                <p>One SELECT statement; preview returns at most 100 rows and 50 columns. Use a read-only SQL login for this connection.</p>
                {queryParameters.length > 0 && (
                  <div className="dataset-parameter-panel">
                    <strong>Query parameters</strong>
                    <p>Values are bound as SQL parameters and are not inserted into the query text.</p>
                    <div className="dataset-parameter-grid">
                      {queryParameters.map((parameterName) => {
                        const parameter = parameterInputs[parameterName] ?? {
                          dataType: 'String' as const,
                          value: '',
                        };
                        return (
                          <div className="dataset-parameter-row" key={parameterName}>
                            <label className="data-source-field">
                              <span>@{parameterName} type</span>
                              <select
                                value={parameter.dataType}
                                onChange={(event) => setParameterInputs((current) => ({
                                  ...current,
                                  [parameterName]: {
                                    ...parameter,
                                    dataType: event.target.value as ParameterType,
                                  },
                                }))}
                              >
                                <option value="String">Text</option>
                                <option value="Integer">Integer</option>
                                <option value="Decimal">Decimal</option>
                                <option value="Boolean">Yes / no</option>
                                <option value="Date">Date</option>
                                <option value="DateTime">Date and time</option>
                              </select>
                            </label>
                            <label className="data-source-field">
                              <span>@{parameterName} preview value</span>
                              {parameter.dataType === 'Boolean' ? (
                                <select
                                  value={parameter.value}
                                  onChange={(event) => setParameterInputs((current) => ({
                                    ...current,
                                    [parameterName]: { ...parameter, value: event.target.value },
                                  }))}
                                >
                                  <option value="">Choose yes or no</option>
                                  <option value="true">Yes</option>
                                  <option value="false">No</option>
                                </select>
                              ) : (
                                <input
                                  type={parameter.dataType === 'Integer' || parameter.dataType === 'Decimal' ? 'number' : parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                                  step={parameter.dataType === 'Decimal' ? 'any' : undefined}
                                  value={parameter.value}
                                  onChange={(event) => setParameterInputs((current) => ({
                                    ...current,
                                    [parameterName]: { ...parameter, value: event.target.value },
                                  }))}
                                />
                              )}
                            </label>
                          </div>
                        );
                      })}
                    </div>
                  </div>
                )}
                <div className="dataset-query-actions">
                  <button className="button button--primary" type="submit" disabled={working || !dataSourceId || !query.trim()}>
                    {working && <span className="spinner spinner--small" />}
                    {working ? 'Running preview…' : 'Preview query'}
                  </button>
                </div>
              </div>
            </div>

            {preview && (
              <div className="dataset-preview">
                <div className="dataset-preview-heading">
                  <div><strong>Query results</strong><span>{preview.rows.length} rows · {preview.columns.length} columns · {preview.durationMilliseconds} ms</span></div>
                  {preview.truncated && <span className="dataset-truncated">Showing first 100 rows</span>}
                </div>
                {preview.columns.length === 0 ? (
                  <p className="dataset-no-columns">The query returned no columns.</p>
                ) : (
                  <div className="dataset-preview-table-wrap">
                    <table className="dataset-preview-table">
                      <thead><tr>{preview.columns.map((column, index) => <th key={`${index}-${column.name}`}><span>{column.name}</span><small>{column.dataType}</small></th>)}</tr></thead>
                      <tbody>
                        {preview.rows.map((row, rowIndex) => (
                          <tr key={rowIndex}>
                            {row.map((cell, columnIndex) => <td key={columnIndex}>{cell === null ? <em>NULL</em> : String(cell)}</td>)}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                    {preview.rows.length === 0 && <p className="dataset-no-columns">The query returned no rows.</p>}
                  </div>
                )}
                <div className="dataset-save-panel">
                  <div className="dataset-save-fields">
                    <label className="data-source-field">
                      <span>Dataset name</span>
                      <input maxLength={200} value={name} onChange={(event) => setName(event.target.value)} placeholder="Monthly customer totals" required />
                    </label>
                    <label className="data-source-field">
                      <span>Description <small>(optional)</small></span>
                      <input maxLength={1000} value={description} onChange={(event) => setDescription(event.target.value)} placeholder="What this dataset contains" />
                    </label>
                  </div>
                  <div className="dataset-save-actions">
                    {editingDatasetId && (
                      <button className="button button--secondary" type="button" disabled={working} onClick={cancelDatasetEdit}>
                        Cancel
                      </button>
                    )}
                    <button className="button button--primary" type="button" disabled={working || !name.trim()} onClick={() => void saveDataset()}>
                      {working ? 'Saving…' : editingDatasetId ? 'Save changes' : 'Save dataset'}
                    </button>
                  </div>
                </div>
              </div>
            )}
          </form>

          <section className="dataset-library" aria-labelledby="dataset-library-title">
            <div className="data-source-list-heading">
              <div><h2 id="dataset-library-title">Available datasets</h2><span>{datasets.length} saved</span></div>
            </div>
            {datasets.length === 0 ? (
              <div className="dataset-library-empty">Datasets you save will be listed here and can be added to report designs.</div>
            ) : (
              <div className="dataset-library-list">
                {datasets.map((dataset) => {
                  const source = sources.find((item) => item.dataSourceId === dataset.dataSourceId);
                  return (
                    <article key={dataset.datasetId}>
                      <span className="data-source-card-icon"><Icon name="file" size={18} /></span>
                      <div><strong>{dataset.name}</strong><span>{dataset.description || 'No description'}</span></div>
                      <small>{source?.name ?? 'Data source'} · {dataset.datasetType}</small>
                      <div className="dataset-library-actions">
                        <button
                          className="button button--secondary"
                          type="button"
                          disabled={working}
                          onClick={() => void editDataset(dataset.datasetId)}
                        >
                          Edit
                        </button>
                        <button
                          className="button button--secondary dataset-design-button"
                          type="button"
                          disabled={working}
                          onClick={() => onDesignDataset(dataset.datasetId)}
                        >
                          Design
                        </button>
                        <button
                          className="button button--secondary dataset-delete-button"
                          type="button"
                          disabled={working || deleting}
                          onClick={() => void prepareDelete(dataset)}
                        >
                          Delete
                        </button>
                      </div>
                    </article>
                  );
                })}
              </div>
            )}
          </section>
        </>
      )}
      {deleteImpact && (
        <div className="dataset-delete-backdrop" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !deleting) setDeleteImpact(null);
        }}>
          <section className="dataset-delete-dialog" role="dialog" aria-modal="true" aria-labelledby="dataset-delete-title">
            <span className="panel-overline">CONFIRM DATASET DELETION</span>
            <h2 id="dataset-delete-title">Delete “{deleteImpact.resourceName}”?</h2>
            <p>This dataset will be deactivated. Reports that use it will also be removed from the workspace and Report Viewer.</p>
            <div className="dataset-delete-impact">
              <strong>Reports affected ({deleteImpact.reportNames.length})</strong>
              {deleteImpact.reportNames.length ? (
                <ul>{deleteImpact.reportNames.map((reportName) => <li key={reportName}>{reportName}</li>)}</ul>
              ) : <p>No active reports use this dataset.</p>}
            </div>
            <div className="dataset-delete-actions">
              <button className="button button--secondary" type="button" disabled={deleting} onClick={() => setDeleteImpact(null)}>Cancel</button>
              <button className="button button--primary dataset-delete-confirm" type="button" disabled={deleting} onClick={() => void deleteDataset()}>
                {deleting && <span className="spinner spinner--small" />}
                {deleting ? 'Deleting…' : 'Delete dataset and affected reports'}
              </button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
