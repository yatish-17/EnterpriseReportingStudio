import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import './DataSourcesPage.css';

interface DataSource {
  dataSourceId: number;
  name: string;
  serverAddress: string;
  databaseName: string;
  authenticationType: 'SqlPassword' | 'Integrated';
  encryptConnection: boolean;
  trustServerCertificate: boolean;
  lastTestSucceeded: boolean | null;
  lastTestedAt: string | null;
  createdAt: string;
  canManage: boolean;
}

interface DeleteImpact {
  dataSourceId: number;
  resourceName: string;
  datasetNames: string[];
  reportNames: string[];
}

interface ConnectionDraft {
  name: string;
  serverAddress: string;
  databaseName: string;
  authenticationType: 'SqlPassword' | 'Integrated';
  userName: string;
  password: string;
  trustServerCertificate: boolean;
}

interface ConnectionTest {
  succeeded: boolean;
  message: string;
  durationMilliseconds: number | null;
}

interface Role {
  roleId: number;
  name: string;
}

interface RolePermission {
  roleId: number;
  roleName: string;
  canUse: boolean;
  canManage: boolean;
}

interface SqlObject {
  schema: string;
  name: string;
  type: string;
}

interface SqlColumn {
  name: string;
  dataType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
}

const emptyDraft: ConnectionDraft = {
  name: '',
  serverAddress: '',
  databaseName: '',
  authenticationType: 'SqlPassword',
  userName: '',
  password: '',
  trustServerCertificate: false,
};

async function readError(response: Response) {
  try {
    const body: unknown = await response.json();
    if (body && typeof body === 'object' && 'title' in body && typeof body.title === 'string') {
      return body.title;
    }
  } catch {
    // The response may not contain a JSON problem document.
  }
  return `The request failed (${response.status}).`;
}

export function DataSourcesPage({ canManage }: { canManage: boolean }) {
  const { accessToken, signOut } = useAuth();
  const [sources, setSources] = useState<DataSource[]>([]);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState<string | null>(null);
  const [draft, setDraft] = useState<ConnectionDraft>(emptyDraft);
  const [showForm, setShowForm] = useState(false);
  const [testing, setTesting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [connectionTest, setConnectionTest] = useState<ConnectionTest | null>(null);
  const [selectedSource, setSelectedSource] = useState<DataSource | null>(null);
  const [schemas, setSchemas] = useState<string[]>([]);
  const [selectedSchema, setSelectedSchema] = useState('');
  const [objects, setObjects] = useState<SqlObject[]>([]);
  const [selectedObject, setSelectedObject] = useState<SqlObject | null>(null);
  const [columns, setColumns] = useState<SqlColumn[]>([]);
  const [exploring, setExploring] = useState(false);
  const [accessSource, setAccessSource] = useState<DataSource | null>(null);
  const [accessRoles, setAccessRoles] = useState<Role[]>([]);
  const [rolePermissions, setRolePermissions] = useState<RolePermission[]>([]);
  const [accessRoleId, setAccessRoleId] = useState('');
  const [accessCanUse, setAccessCanUse] = useState(false);
  const [accessCanManage, setAccessCanManage] = useState(false);
  const [accessLoading, setAccessLoading] = useState(false);
  const [accessSaving, setAccessSaving] = useState(false);
  const [accessNotice, setAccessNotice] = useState<string | null>(null);
  const [deleteImpact, setDeleteImpact] = useState<DeleteImpact | null>(null);
  const [deleting, setDeleting] = useState(false);

  const request = useCallback(async <T,>(url: string, init?: RequestInit): Promise<T> => {
    if (!accessToken) throw new Error('Your session has expired. Sign in again to continue.');
    const response = await fetch(url, {
      ...init,
      headers: {
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        Authorization: `Bearer ${accessToken}`,
        ...init?.headers,
      },
    });
    if (response.status === 401) {
      await signOut();
      throw new Error('Your session has expired. Sign in again to continue.');
    }
    if (!response.ok) throw new Error(await readError(response));
    if (response.status === 204) return undefined as T;
    return response.json() as Promise<T>;
  }, [accessToken, signOut]);

  const loadSources = useCallback(async () => {
    setLoading(true);
    setPageError(null);
    try {
      setSources(await request<DataSource[]>('/api/data-sources'));
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'Data sources could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [request]);

  useEffect(() => {
    void loadSources();
  }, [loadSources]);

  async function handleTestConnection(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setTesting(true);
    setConnectionTest(null);
    setPageError(null);
    try {
      const result = await request<ConnectionTest>('/api/data-sources/test-connection', {
        method: 'POST',
        body: JSON.stringify(draft),
      });
      setConnectionTest(result);
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'The connection test failed.');
    } finally {
      setTesting(false);
    }
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!connectionTest?.succeeded) return;
    setSaving(true);
    setPageError(null);
    try {
      await request<DataSource>('/api/data-sources', {
        method: 'POST',
        body: JSON.stringify(draft),
      });
      setDraft(emptyDraft);
      setConnectionTest(null);
      setShowForm(false);
      await loadSources();
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'The data source could not be saved.');
    } finally {
      setSaving(false);
    }
  }

  async function openExplorer(source: DataSource) {
    setSelectedSource(source);
    setSelectedSchema('');
    setObjects([]);
    setSelectedObject(null);
    setColumns([]);
    setExploring(true);
    setPageError(null);
    try {
      const schemaRows = await request<{ name: string }[]>(
        `/api/data-sources/${source.dataSourceId}/schemas`,
      );
      setSchemas(schemaRows.map((row) => row.name));
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'Database metadata could not be loaded.');
      setSchemas([]);
    } finally {
      setExploring(false);
    }
  }

  async function chooseSchema(schema: string) {
    if (!selectedSource) return;
    setSelectedSchema(schema);
    setSelectedObject(null);
    setColumns([]);
    setExploring(true);
    setPageError(null);
    try {
      const rows = await request<SqlObject[]>(
        `/api/data-sources/${selectedSource.dataSourceId}/schemas/${encodeURIComponent(schema)}/objects`,
      );
      setObjects(rows);
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'Objects could not be loaded.');
      setObjects([]);
    } finally {
      setExploring(false);
    }
  }

  async function chooseObject(object: SqlObject) {
    if (!selectedSource) return;
    setSelectedObject(object);
    setExploring(true);
    setPageError(null);
    try {
      setColumns(await request<SqlColumn[]>(
        `/api/data-sources/${selectedSource.dataSourceId}/schemas/${encodeURIComponent(object.schema)}/objects/${encodeURIComponent(object.name)}/columns`,
      ));
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'Columns could not be loaded.');
      setColumns([]);
    } finally {
      setExploring(false);
    }
  }

  async function testSavedSource(source: DataSource) {
    setPageError(null);
    try {
      const result = await request<ConnectionTest>(`/api/data-sources/${source.dataSourceId}/test`, {
        method: 'POST',
      });
      if (!result.succeeded) setPageError(result.message);
      await loadSources();
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'The connection test failed.');
    }
  }

  async function prepareDelete(source: DataSource) {
    setPageError(null);
    try {
      const impact = await request<Omit<DeleteImpact, 'dataSourceId'>>(
        `/api/data-sources/${source.dataSourceId}/delete-impact`,
      );
      setDeleteImpact({ ...impact, dataSourceId: source.dataSourceId });
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'The connection impact could not be loaded.');
    }
  }

  async function deleteSource() {
    if (!deleteImpact) return;
    setDeleting(true);
    setPageError(null);
    try {
      await request<void>(`/api/data-sources/${deleteImpact.dataSourceId}`, { method: 'DELETE' });
      setDeleteImpact(null);
      if (selectedSource?.dataSourceId === deleteImpact.dataSourceId) {
        setSelectedSource(null);
        setSchemas([]);
        setObjects([]);
        setColumns([]);
      }
      if (accessSource?.dataSourceId === deleteImpact.dataSourceId) setAccessSource(null);
      await loadSources();
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'The connection could not be deleted.');
    } finally {
      setDeleting(false);
    }
  }

  async function openAccess(source: DataSource) {
    setAccessSource(source);
    setAccessRoles([]);
    setRolePermissions([]);
    setAccessRoleId('');
    setAccessCanUse(false);
    setAccessCanManage(false);
    setAccessNotice(null);
    setPageError(null);
    setAccessLoading(true);
    try {
      const permissions = await request<RolePermission[]>(
        `/api/data-sources/${source.dataSourceId}/permissions/roles`,
      );
      setAccessRoles(permissions.map(({ roleId, roleName }) => ({ roleId, name: roleName })));
      setRolePermissions(permissions);
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'Role access could not be loaded.');
    } finally {
      setAccessLoading(false);
    }
  }

  function selectAccessRole(value: string) {
    setAccessRoleId(value);
    setAccessNotice(null);
    const permission = rolePermissions.find((item) => item.roleId === Number(value));
    setAccessCanUse(permission?.canUse ?? false);
    setAccessCanManage(permission?.canManage ?? false);
  }

  async function saveAccess() {
    if (!accessSource || !accessRoleId) return;
    setAccessSaving(true);
    setPageError(null);
    setAccessNotice(null);
    try {
      await request<void>(
        `/api/data-sources/${accessSource.dataSourceId}/permissions/roles/${accessRoleId}`,
        {
          method: 'PUT',
          body: JSON.stringify({ canUse: accessCanUse, canManage: accessCanManage }),
        },
      );
      const permissions = await request<RolePermission[]>(
        `/api/data-sources/${accessSource.dataSourceId}/permissions/roles`,
      );
      setRolePermissions(permissions);
      setAccessNotice(accessCanUse
        ? 'Role access saved. Members can now use this SQL connection.'
        : 'Access revoked for this role.');
    } catch (error) {
      setPageError(error instanceof Error ? error.message : 'Role access could not be saved.');
    } finally {
      setAccessSaving(false);
    }
  }

  function updateDraft<K extends keyof ConnectionDraft>(key: K, value: ConnectionDraft[K]) {
    setDraft((current) => ({ ...current, [key]: value }));
    setConnectionTest(null);
  }

  return (
    <section className="data-sources-page" aria-labelledby="data-sources-title">
      <div className="data-sources-heading">
        <div>
          <span className="login-kicker">CONNECTIONS</span>
          <h1 id="data-sources-title">Data sources</h1>
          <p>Connect to SQL Server databases and inspect the objects your account can read.</p>
        </div>
        {canManage && (
          <button
            className="button button--primary data-source-add"
            type="button"
            onClick={() => {
              if (showForm) {
                setDraft(emptyDraft);
                setConnectionTest(null);
              }
              setShowForm(!showForm);
              setPageError(null);
            }}
          >
            <Icon name={showForm ? 'x' : 'plus'} size={17} />
            {showForm ? 'Close form' : 'Add SQL Server'}
          </button>
        )}
      </div>

      {pageError && (
        <div className="data-source-alert" role="alert">
          <Icon name="x" size={17} />
          <span>{pageError}</span>
          <button type="button" onClick={() => setPageError(null)} aria-label="Dismiss error">
            <Icon name="x" size={16} />
          </button>
        </div>
      )}

      {showForm && canManage && (
        <form className="data-source-form" onSubmit={connectionTest?.succeeded ? handleCreate : handleTestConnection}>
          <div className="data-source-form-heading">
            <div>
              <h2>Connect a SQL Server</h2>
              <p>Credentials are protected by the API and never returned to the browser.</p>
            </div>
            <span className="data-source-secure-label"><Icon name="lock" size={15} /> Encrypted connection</span>
          </div>
          <div className="data-source-form-grid">
            <label className="data-source-field">
              <span>Connection name</span>
              <input required maxLength={200} value={draft.name} onChange={(event) => updateDraft('name', event.target.value)} placeholder="Finance reporting" />
            </label>
            <label className="data-source-field">
              <span>SQL Server</span>
              <input required maxLength={500} value={draft.serverAddress} onChange={(event) => updateDraft('serverAddress', event.target.value)} placeholder="server,1433 or host\\instance" />
            </label>
            <label className="data-source-field">
              <span>Database</span>
              <input required maxLength={256} value={draft.databaseName} onChange={(event) => updateDraft('databaseName', event.target.value)} placeholder="Database name" />
            </label>
            <label className="data-source-field">
              <span>Authentication</span>
              <select value={draft.authenticationType} onChange={(event) => updateDraft('authenticationType', event.target.value as ConnectionDraft['authenticationType'])}>
                <option value="SqlPassword">SQL Server authentication</option>
                <option value="Integrated">Windows integrated authentication</option>
              </select>
            </label>
            {draft.authenticationType === 'SqlPassword' && (
              <>
                <label className="data-source-field">
                  <span>SQL username</span>
                  <input required maxLength={256} autoComplete="off" value={draft.userName} onChange={(event) => updateDraft('userName', event.target.value)} />
                </label>
                <label className="data-source-field">
                  <span>SQL password</span>
                  <input required maxLength={2048} type="password" autoComplete="new-password" value={draft.password} onChange={(event) => updateDraft('password', event.target.value)} />
                </label>
              </>
            )}
          </div>
          <label className="data-source-checkbox">
            <input type="checkbox" checked={draft.trustServerCertificate} onChange={(event) => updateDraft('trustServerCertificate', event.target.checked)} />
            <span><strong>Trust the server certificate</strong><small>Use only for trusted development servers with a self-signed certificate.</small></span>
          </label>
          {connectionTest && (
            <div className={`data-source-test-result${connectionTest.succeeded ? ' is-success' : ' is-failure'}`} role="status">
              <Icon name={connectionTest.succeeded ? 'check' : 'x'} size={17} />
              <span>{connectionTest.message}{connectionTest.durationMilliseconds !== null && ` (${connectionTest.durationMilliseconds} ms)`}</span>
            </div>
          )}
          <div className="data-source-form-actions">
            <button className="button button--secondary" type="button" onClick={() => { setShowForm(false); setDraft(emptyDraft); setConnectionTest(null); }}>
              Cancel
            </button>
            <button className="button button--primary" type="submit" disabled={testing || saving}>
              {(testing || saving) && <span className="spinner spinner--small" />}
              {testing ? 'Testing connection…' : saving ? 'Saving…' : connectionTest?.succeeded ? 'Save connection' : 'Test connection'}
            </button>
          </div>
        </form>
      )}

      <div className="data-source-list-heading">
        <div>
          <h2>Organization connections</h2>
          <span>{loading ? 'Loading…' : `${sources.length} ${sources.length === 1 ? 'connection' : 'connections'}`}</span>
        </div>
        <button className="data-source-refresh" type="button" onClick={() => void loadSources()} disabled={loading}>
          <Icon name="activity" size={16} /> Refresh
        </button>
      </div>

      {loading ? (
        <div className="data-source-empty"><span className="spinner spinner--large" /><p>Loading available connections…</p></div>
      ) : sources.length === 0 ? (
        <div className="data-source-empty">
          <span className="data-source-empty-icon"><Icon name="database" size={22} /></span>
          <h3>No data sources connected</h3>
          <p>{canManage ? 'Add a SQL Server connection to inspect its tables and begin creating datasets.' : 'An administrator can add connections and grant your account access.'}</p>
          {canManage && !showForm && <button className="button button--secondary" type="button" onClick={() => setShowForm(true)}>Add your first connection</button>}
        </div>
      ) : (
        <div className="data-source-layout">
          <div className="data-source-list-column">
            <div className="data-source-list">
              {sources.map((source) => (
                <article className={`data-source-card${selectedSource?.dataSourceId === source.dataSourceId ? ' is-selected' : ''}`} key={source.dataSourceId}>
                  <span className="data-source-card-icon"><Icon name="database" size={19} /></span>
                  <div className="data-source-card-copy">
                    <strong>{source.name}</strong>
                    <span>{source.serverAddress} <span aria-hidden="true">·</span> {source.databaseName}</span>
                    <small>
                      {source.lastTestSucceeded === true ? 'Connection verified' : source.lastTestSucceeded === false ? 'Last connection failed' : 'Not tested'}
                      {source.lastTestedAt && ` · ${new Date(source.lastTestedAt).toLocaleString()}`}
                    </small>
                  </div>
                  <div className="data-source-card-actions">
                    <button type="button" className="data-source-icon-action" onClick={() => void testSavedSource(source)} aria-label={`Test ${source.name} connection`} title="Test connection">
                      <Icon name="activity" size={17} />
                    </button>
                    {source.canManage && (
                      <button
                        type="button"
                        className={`data-source-icon-action${accessSource?.dataSourceId === source.dataSourceId ? ' is-active' : ''}`}
                        onClick={() => void openAccess(source)}
                        aria-label={`Manage ${source.name} role access`}
                        title="Manage role access"
                      >
                        <Icon name="users" size={17} />
                      </button>
                    )}
                    <button type="button" className="button button--secondary data-source-explore" onClick={() => void openExplorer(source)}>
                      Explore
                    </button>
                    {source.canManage && (
                      <button
                        type="button"
                        className="button button--secondary data-source-delete"
                        disabled={deleting}
                        onClick={() => void prepareDelete(source)}
                      >
                        Delete
                      </button>
                    )}
                  </div>
                </article>
              ))}
            </div>
            {accessSource && (
              <section className="data-source-access-panel" aria-labelledby="data-source-access-title">
                <header>
                  <div><span className="panel-overline">ROLE ACCESS</span><h3 id="data-source-access-title">{accessSource.name}</h3></div>
                  <button
                    type="button"
                    className="data-source-icon-action"
                    onClick={() => { setAccessSource(null); setAccessNotice(null); }}
                    aria-label="Close role access"
                  ><Icon name="x" size={17} /></button>
                </header>
                {accessLoading ? (
                  <div className="data-source-access-loading"><span className="spinner spinner--small" /> Loading role permissions…</div>
                ) : (
                  <div className="data-source-access-content">
                    <label className="data-source-field">
                      <span>Organization role</span>
                      <select value={accessRoleId} onChange={(event) => selectAccessRole(event.target.value)}>
                        <option value="">Choose a role</option>
                        {accessRoles.map((role) => <option key={role.roleId} value={role.roleId}>{role.name}</option>)}
                      </select>
                    </label>
                    <label className="data-source-checkbox">
                      <input type="checkbox" checked={accessCanUse} disabled={!accessRoleId} onChange={(event) => { setAccessCanUse(event.target.checked); if (!event.target.checked) setAccessCanManage(false); }} />
                      <span><strong>Can use this connection</strong><small>Members can inspect metadata and build datasets.</small></span>
                    </label>
                    <label className="data-source-checkbox">
                      <input type="checkbox" checked={accessCanManage} disabled={!accessRoleId || !accessCanUse} onChange={(event) => setAccessCanManage(event.target.checked)} />
                      <span><strong>Can manage access</strong><small>Members can grant or revoke access for other roles.</small></span>
                    </label>
                    <div className="data-source-access-actions">
                      <span>{accessNotice && <span className="data-source-access-notice" role="status">{accessNotice}</span>}</span>
                      <button className="button button--secondary" type="button" disabled={!accessRoleId || accessSaving} onClick={() => void saveAccess()}>
                        {accessSaving && <span className="spinner spinner--small" />}
                        {accessCanUse ? 'Save role access' : 'Revoke access'}
                      </button>
                    </div>
                  </div>
                )}
              </section>
            )}
          </div>
          {selectedSource && (
            <aside className="data-source-explorer" aria-label="Database explorer">
              <div className="data-source-explorer-heading">
                <div><span className="panel-overline">DATABASE EXPLORER</span><h3>{selectedSource.name}</h3></div>
                <button type="button" className="data-source-icon-action" onClick={() => { setSelectedSource(null); setSchemas([]); setObjects([]); setColumns([]); }} aria-label="Close explorer"><Icon name="x" size={17} /></button>
              </div>
              {exploring && <div className="data-source-explorer-loading"><span className="spinner spinner--small" /> Loading metadata…</div>}
              {!exploring && schemas.length === 0 ? (
                <p className="data-source-explorer-empty">No readable schemas were returned for this connection.</p>
              ) : (
                <div className="data-source-tree">
                  <label className="data-source-field">
                    <span>Schema</span>
                    <select value={selectedSchema} onChange={(event) => void chooseSchema(event.target.value)}>
                      <option value="">Choose a schema</option>
                      {schemas.map((schema) => <option key={schema} value={schema}>{schema}</option>)}
                    </select>
                  </label>
                  {selectedSchema && (
                    <div className="data-source-object-list">
                      {objects.map((object) => (
                        <button type="button" key={`${object.schema}.${object.name}`} className={selectedObject?.name === object.name ? 'is-selected' : ''} onClick={() => void chooseObject(object)}>
                          <Icon name={object.type === 'View' ? 'eye' : 'database'} size={15} />
                          <span>{object.name}</span><small>{object.type}</small>
                        </button>
                      ))}
                      {!exploring && objects.length === 0 && <p>No readable tables or views in this schema.</p>}
                    </div>
                  )}
                  {selectedObject && (
                    <div className="data-source-columns">
                      <strong>{selectedObject.name} columns</strong>
                      {columns.map((column) => (
                        <div className="data-source-column" key={column.name}>
                          <span>{column.name}{column.isPrimaryKey && <small>PK</small>}</span>
                          <code>{column.dataType}{column.isNullable ? '?' : ''}</code>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </aside>
          )}
        </div>
      )}
      {deleteImpact && (
        <div className="data-source-delete-backdrop" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !deleting) setDeleteImpact(null);
        }}>
          <section className="data-source-delete-dialog" role="dialog" aria-modal="true" aria-labelledby="data-source-delete-title">
            <span className="panel-overline">CONFIRM CONNECTION DELETION</span>
            <h2 id="data-source-delete-title">Delete “{deleteImpact.resourceName}”?</h2>
            <p>This removes the saved connection from Enterprise Reporting Studio. It does not drop or change the external SQL Server database. Datasets and reports that depend on this connection will also be deactivated.</p>
            <div className="data-source-delete-impact">
              <strong>Datasets affected ({deleteImpact.datasetNames.length})</strong>
              {deleteImpact.datasetNames.length
                ? <ul>{deleteImpact.datasetNames.map((datasetName) => <li key={datasetName}>{datasetName}</li>)}</ul>
                : <p>No active datasets use this connection.</p>}
            </div>
            <div className="data-source-delete-impact">
              <strong>Reports affected ({deleteImpact.reportNames.length})</strong>
              {deleteImpact.reportNames.length
                ? <ul>{deleteImpact.reportNames.map((reportName) => <li key={reportName}>{reportName}</li>)}</ul>
                : <p>No active reports use this connection.</p>}
            </div>
            <div className="data-source-delete-actions">
              <button className="button button--secondary" type="button" disabled={deleting} onClick={() => setDeleteImpact(null)}>Cancel</button>
              <button className="button button--primary data-source-delete-confirm" type="button" disabled={deleting} onClick={() => void deleteSource()}>
                {deleting && <span className="spinner spinner--small" />}
                {deleting ? 'Deleting…' : 'Delete connection and affected reports'}
              </button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
