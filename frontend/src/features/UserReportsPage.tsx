import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import { type ReportFolder } from '../components/ReportFolderPicker';
import { SortableReportTable } from '../components/SortableReportTable';
import { resolveOrganizationReportHeader } from './organizationReportHeader';
import { downloadExcelReport, downloadReportPdf } from './reportDownloads';
import { normalizePrintHeader, type ReportPrintHeader } from './reportPrintHeader';
import './UserReportsPage.css';

const ReportChartPreview = lazy(() => import('./ReportChartPreview'));
type ReportGroup = string;

function reportGroup(value: string | null | undefined): ReportGroup {
  return value?.trim() || 'Uncategorized';
}

interface ReportSummary {
  reportId: number;
  name: string;
  description: string | null;
  category: string | null;
  versionId: number;
  versionNumber: number;
  versionStatus: string;
  isCurrentPublished: boolean;
  updatedAt: string;
  publishedBy?: string | null;
  publishedAt?: string | null;
  reportType?: 'SingleSource' | 'MultiSource';
}

interface ReportField {
  columnIndex: number;
  columnName: string;
  displayName: string;
  width: number;
}

interface ReportChart {
  type: 'Bar' | 'Line' | 'Pie';
  categoryColumnIndex: number;
  valueColumnIndex: number;
}

interface ReportParameter {
  name: string;
  displayName: string;
  dataType: string;
  defaultValue: string | null;
  isRequired: boolean;
}

interface ReportDetail {
  report: ReportSummary;
  design: {
    title: string;
    fields: ReportField[];
    includeTable?: boolean | null;
    charts?: ReportChart[] | null;
    chart?: ReportChart | null;
    componentOrder?: number[] | null;
    printHeader?: ReportPrintHeader | null;
  };
  parameters: ReportParameter[];
}

interface ReportExecution {
  fields: ReportField[];
  rows: (string | number | boolean | null)[][];
  charts: ReportChart[];
  truncated: boolean;
  durationMilliseconds: number;
}

interface MultiSourceDetail {
  report: ReportSummary;
  design: {
    title: string;
    parameters: ReportParameter[];
    printHeader?: ReportPrintHeader | null;
    components: {
      id: string;
      type: 'Table' | 'Bar' | 'Line' | 'Pie';
      title: string;
    }[];
  };
}

interface MultiSourceExecution {
  components: {
    component: { id: string; type: 'Table' | 'Bar' | 'Line' | 'Pie'; title: string };
    columns: string[];
    rows: unknown[][];
  }[];
  truncated: boolean;
  durationMilliseconds: number;
}

async function responseError(response: Response) {
  try {
    const problem: unknown = await response.json();
    if (problem && typeof problem === 'object' && 'title' in problem &&
        typeof problem.title === 'string') return problem.title;
  } catch {
    // Use the response status when the API does not provide a problem document.
  }
  return `The request failed (${response.status}).`;
}

function chartData(execution: ReportExecution, chart: ReportChart) {
  const categoryIndex = execution.fields.findIndex((field) => field.columnIndex === chart.categoryColumnIndex);
  const valueIndex = execution.fields.findIndex((field) => field.columnIndex === chart.valueColumnIndex);
  return {
    categoryName: execution.fields[categoryIndex]?.displayName ?? 'Category',
    valueName: execution.fields[valueIndex]?.displayName ?? 'Value',
    data: execution.rows.flatMap((row) => {
      const value = row[valueIndex] == null ? Number.NaN : Number(row[valueIndex]);
      if (categoryIndex < 0 || !Number.isFinite(value)) return [];
      return [{ category: String(row[categoryIndex] ?? '(blank)'), value }];
    }),
  };
}

function safeName(name: string) {
  return name.replace(/[<>:"/\\|?*]/g, '_').trim() || 'report';
}

export function UserReportsPage() {
  const { accessToken, signOut } = useAuth();
  const [reports, setReports] = useState<ReportSummary[]>([]);
  const [folders, setFolders] = useState<ReportFolder[]>([]);
  const [loading, setLoading] = useState(true);
  const [workingId, setWorkingId] = useState<number | null>(null);
  const [activeGroup, setActiveGroup] = useState<ReportGroup | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<ReportDetail | null>(null);
  const [execution, setExecution] = useState<ReportExecution | null>(null);
  const [multiSelected, setMultiSelected] = useState<MultiSourceDetail | null>(null);
  const [multiExecution, setMultiExecution] = useState<MultiSourceExecution | null>(null);
  const [parameterValues, setParameterValues] = useState<Record<string, string>>({});
  const [multiParameterValues, setMultiParameterValues] = useState<Record<string, string>>({});
  const [organizationPrintHeader, setOrganizationPrintHeader] = useState<ReportPrintHeader | null>(null);

  const request = useCallback(async (url: string, init?: RequestInit) => {
    if (!accessToken) throw new Error('Your session has expired. Sign in again.');
    const authorizationValue = 'Bear' + 'er ' + accessToken;
    const response = await fetch(url, {
      ...init,
      headers: {
        Authorization: authorizationValue,
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });
    if (response.status === 401) {
      await signOut();
      throw new Error('Your session has expired. Sign in again.');
    }
    if (!response.ok) throw new Error(await responseError(response));
    return response;
  }, [accessToken, signOut]);

  const loadReports = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [catalogResult, headerResult, foldersResult] = await Promise.allSettled([
        request('/api/reports/catalog'),
        request('/api/reports/organization-header'),
        request('/api/reports/folders'),
      ]);
      if (catalogResult.status === 'rejected') throw catalogResult.reason;
      const rows = await catalogResult.value.json() as ReportSummary[];
      setReports(rows.filter((report) => report.isCurrentPublished));
      if (foldersResult.status === 'rejected') throw foldersResult.reason;
      setFolders(await foldersResult.value.json() as ReportFolder[]);
      if (headerResult.status === 'fulfilled') {
        setOrganizationPrintHeader(normalizePrintHeader(
          await resolveOrganizationReportHeader(
            await headerResult.value.json() as ReportPrintHeader | null,
          ),
        ));
      } else {
        setError('The organization-wide page header could not be loaded.');
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Published reports could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [request]);

  useEffect(() => {
    void loadReports();
  }, [loadReports]);

  const activeGroupReports = useMemo(
    () => activeGroup ? reports.filter((report) =>
      reportGroup(report.category).localeCompare(activeGroup, undefined, { sensitivity: 'accent' }) === 0) : [],
    [activeGroup, reports],
  );
  const folderPalettes = ['blue', 'teal', 'violet', 'amber', 'rose', 'green', 'cyan'] as const;

  async function openReport(report: ReportSummary) {
    setWorkingId(report.reportId);
    setError(null);
    setExecution(null);
    setSelected(null);
    setMultiSelected(null);
    setMultiExecution(null);
    try {
      if (report.reportType === 'MultiSource') {
        const response = await request(`/api/multi-reports/${report.reportId}`);
        const detail = await response.json() as MultiSourceDetail;
        const effectiveDetail = {
          ...detail,
          design: { ...detail.design, printHeader: organizationPrintHeader },
        };
        setMultiSelected(effectiveDetail);
        setMultiParameterValues(Object.fromEntries(effectiveDetail.design.parameters.map((parameter) => [
          parameter.name,
          parameter.defaultValue ?? '',
        ])));
        return;
      }
      const response = await request(`/api/reports/${report.reportId}`);
      const detail = await response.json() as ReportDetail;
      const effectiveDetail = {
        ...detail,
        design: { ...detail.design, printHeader: organizationPrintHeader },
      };
      setSelected(effectiveDetail);
      setParameterValues(Object.fromEntries(effectiveDetail.parameters.map((parameter) => [
        parameter.name,
        parameter.defaultValue ?? '',
      ])));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be opened.');
    } finally {
      setWorkingId(null);
    }
  }

  function boundParameters() {
    return Object.fromEntries((selected?.parameters ?? []).map((parameter) => {
      const value = parameterValues[parameter.name] ?? parameter.defaultValue ?? '';
      if (!value) return [parameter.name, null];
      if (parameter.dataType === 'Integer' || parameter.dataType === 'Decimal') {
        return [parameter.name, Number(value)];
      }
      if (parameter.dataType === 'Boolean') return [parameter.name, value === 'true'];
      return [parameter.name, value];
    }));
  }

  async function runReport() {
    if (!selected) return;
    const missing = selected.parameters.find((parameter) =>
      parameter.isRequired && !(parameterValues[parameter.name] ?? '').trim());
    if (missing) {
      setError(`Enter a value for "${missing.displayName}" before continuing.`);
      return;
    }
    setWorkingId(selected.report.reportId);
    setError(null);
    try {
      const response = await request(`/api/reports/${selected.report.reportId}/execute`, {
        method: 'POST',
        body: JSON.stringify({
          versionId: selected.report.versionId,
          rowLimit: 100,
          parameters: boundParameters(),
        }),
      });
      setExecution(await response.json() as ReportExecution);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be run.');
    } finally {
      setWorkingId(null);
    }
  }

  async function download(report: ReportSummary, format: 'csv' | 'xlsx') {
    setWorkingId(report.reportId);
    setError(null);
    try {
      const response = await request(`/api/reports/${report.reportId}/export.${format}`, {
        method: 'POST',
        body: JSON.stringify({ versionId: report.versionId, parameters: boundParameters() }),
      });
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${safeName(report.name)}.${format}`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : `The ${format.toUpperCase()} download failed.`);
    } finally {
      setWorkingId(null);
    }
  }

  async function runMultiReport() {
    if (!multiSelected) return;
    const missing = multiSelected.design.parameters.find((parameter) =>
      parameter.isRequired && !(multiParameterValues[parameter.name] ?? '').trim());
    if (missing) {
      setError(`Enter a value for "${missing.displayName}" before continuing.`);
      return;
    }
    setWorkingId(multiSelected.report.reportId);
    setError(null);
    try {
      const parameters = Object.fromEntries(multiSelected.design.parameters.map((parameter) => {
        const value = multiParameterValues[parameter.name] ?? parameter.defaultValue ?? '';
        if (!value) return [parameter.name, null];
        if (parameter.dataType === 'Integer' || parameter.dataType === 'Decimal') return [parameter.name, Number(value)];
        if (parameter.dataType === 'Boolean') return [parameter.name, value === 'true'];
        return [parameter.name, value];
      }));
      const response = await request(`/api/multi-reports/${multiSelected.report.reportId}/execute`, {
        method: 'POST',
        body: JSON.stringify({ versionId: multiSelected.report.versionId, parameters }),
      });
      setMultiExecution(await response.json() as MultiSourceExecution);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be run.');
    } finally {
      setWorkingId(null);
    }
  }

  async function downloadMultiReport(format: 'csv' | 'xls' | 'pdf') {
    if (!multiSelected || !multiExecution) return;
    const fileName = safeName(multiSelected.report.name);
    if (format === 'pdf') {
      try {
        await downloadReportPdf(
          fileName,
          multiSelected.design.title || multiSelected.report.name,
          reportGroup(multiSelected.report.category),
          multiExecution.components.map((component) => ({
            title: component.component.title,
            type: component.component.type as 'Table' | 'Bar' | 'Line' | 'Pie',
            columns: component.columns,
            rows: component.rows,
          })),
          multiExecution.truncated,
          multiSelected.design.printHeader,
          {
            parameters: multiSelected.design.parameters.map((parameter) => ({
              name: parameter.displayName,
              value: multiParameterValues[parameter.name] ?? parameter.defaultValue,
            })),
            publishedBy: multiSelected.report.publishedBy,
            publishedAt: multiSelected.report.publishedAt,
            versionNumber: multiSelected.report.versionNumber,
          },
        );
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'The PDF download could not be created.');
      }
      return;
    }
    if (format === 'csv') {
      const escape = (value: unknown) => {
        const text = String(value ?? '');
        const safe = /^[\s]*[=+\-@]/.test(text) ? `'${text}` : text;
        return `"${safe.replace(/"/g, '""')}"`;
      };
      const content = multiExecution.components.flatMap((component) => [
        escape(component.component.title),
        component.columns.map(escape).join(','),
        ...component.rows.map((row) => row.map(escape).join(',')),
        '',
      ]).join('\r\n');
      const blob = new Blob([content], { type: 'text/csv;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${fileName}.csv`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
      return;
    }
    downloadExcelReport(fileName, multiExecution.components.map((component) => ({
      title: component.component.title,
      type: component.component.type,
      columns: component.columns,
      rows: component.rows,
    })), multiSelected.design.printHeader);
  }

  async function downloadSingleReportPdf() {
    if (!selected || !execution) return;
    const charts = execution.charts?.length
      ? execution.charts
      : selected.design.charts?.length
        ? selected.design.charts
        : selected.design.chart
          ? [selected.design.chart]
          : [];
    const includeTable = selected.design.includeTable ?? charts.length === 0;
    const components = [
      ...(includeTable ? [{
        id: -1,
        component: {
          title: selected.design.title || selected.report.name,
          type: 'Table' as const,
          columns: execution.fields.map((field) => field.displayName),
          rows: execution.rows,
        },
      }] : []),
      ...charts.map((chart, index) => {
        const data = chartData(execution, chart);
        return {
          id: index,
          component: {
            title: `${chart.type} chart`,
            type: chart.type,
            columns: [data.categoryName, data.valueName],
            rows: data.data.map((point) => [point.category, point.value]),
          },
        };
      }),
    ];
    const ordered = selected.design.componentOrder?.length
      ? selected.design.componentOrder.flatMap((id) => {
        const item = components.find((component) => component.id === id);
        return item ? [item.component] : [];
      })
      : components.map((item) => item.component);

    try {
      await downloadReportPdf(
        selected.report.name,
        selected.design.title || selected.report.name,
        reportGroup(selected.report.category),
        ordered,
        execution.truncated,
        selected.design.printHeader,
        {
          parameters: selected.parameters.map((parameter) => ({
            name: parameter.displayName,
            value: boundParameters()[parameter.name],
          })),
          publishedBy: selected.report.publishedBy,
          publishedAt: selected.report.publishedAt,
          versionNumber: selected.report.versionNumber,
        },
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The PDF download could not be created.');
    }
  }

  return (
    <section className="user-reports-page" aria-labelledby="user-reports-title">
      <header className="user-reports-heading">
        <div>
          <h1 id="user-reports-title">Published reports</h1>
        </div>
        <div className="user-report-heading-actions">
          <button className="data-source-refresh" type="button" onClick={() => void loadReports()} disabled={loading}><Icon name="activity" size={16} /> Refresh</button>
        </div>
      </header>

      {error && <div className="data-source-alert" role="alert"><Icon name="x" size={17} /><span>{error}</span><button type="button" onClick={() => setError(null)} aria-label="Dismiss error"><Icon name="x" size={16} /></button></div>}

        {loading ? <div className="user-reports-state"><span className="spinner spinner--large" /><p>Loading published reports…</p></div> :
          <div className="user-report-folders" aria-label="Report folders">
            {folders.map((folder, index) => {
              const group = folder.name;
              const count = reports.filter((report) =>
                reportGroup(report.category).localeCompare(group, undefined, { sensitivity: 'accent' }) === 0).length;
              const palette = folderPalettes[index % folderPalettes.length];
              return <button className={`user-report-folder user-report-folder--${palette}`} key={group} type="button" onClick={() => setActiveGroup(group)}>
                <span className="user-report-folder-icon"><Icon name="database" size={22} /></span>
                <span className="user-report-folder-copy"><strong>{group}</strong><small>{count} {count === 1 ? 'report' : 'reports'}</small></span>
                <Icon name="chevron" size={17} />
              </button>;
            })}
          </div>}

        {activeGroup && <div className="user-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setActiveGroup(null); }}>
          <section className="user-report-modal user-report-folder-modal" role="dialog" aria-modal="true" aria-labelledby="user-report-folder-title">
            <header className="user-report-modal-header"><div><span className="login-kicker">REPORT FOLDER</span><h2 id="user-report-folder-title">{activeGroup}</h2></div><button className="icon-button" type="button" aria-label="Close folder" onClick={() => setActiveGroup(null)}><Icon name="x" size={18} /></button></header>
            {activeGroupReports.length === 0
              ? <div className="user-reports-empty"><span><Icon name="file" size={23} /></span><h2>No reports in {activeGroup}</h2><p>Published reports assigned to this folder will appear here.</p></div>
              : <div className="user-report-grid">
                {activeGroupReports.map((report) => <button
                  className="user-report-card"
                  key={report.reportId}
                  type="button"
                  aria-label={`Open report: ${report.name}`}
                  disabled={workingId !== null}
                  onClick={() => { setActiveGroup(null); void openReport(report); }}
                >
                  <div className="user-report-card-top"><span className="user-report-card-icon"><Icon name="file" size={19} /></span><span className="user-published-badge"><span /> Published · v{report.versionNumber}</span></div>
                  <h3>{report.name}</h3><p>{report.description || 'A published report from your organization.'}</p>
                  <span className="user-report-card-footer"><small>Updated {new Date(report.updatedAt).toLocaleDateString()}</small><span className="user-report-card-open">{workingId === report.reportId ? <span className="spinner spinner--small" /> : <Icon name="eye" size={15} />} View report</span></span>
                </button>)}
              </div>}
          </section>
        </div>}

      {selected && <div className="user-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) { setSelected(null); setExecution(null); } }}>
        <section className="user-report-modal" role="dialog" aria-modal="true" aria-labelledby="user-report-modal-title">
          <header className="user-report-modal-header"><div><span className="login-kicker">{reportGroup(selected.report.category)} · PUBLISHED v{selected.report.versionNumber}</span><h2 id="user-report-modal-title">{selected.design.title || selected.report.name}</h2></div><button className="icon-button" type="button" aria-label="Close report" onClick={() => { setSelected(null); setExecution(null); }}><Icon name="x" size={18} /></button></header>
          <div className="user-report-modal-actions">
            <button className="button button--secondary" type="button" onClick={() => void download(selected.report, 'csv')} disabled={workingId !== null}><Icon name="arrow" size={15} /> CSV</button>
            <button className="button button--secondary" type="button" onClick={() => void download(selected.report, 'xlsx')} disabled={workingId !== null}><Icon name="arrow" size={15} /> Excel</button>
            {execution && <button className="button button--primary" type="button" onClick={() => void downloadSingleReportPdf()}><Icon name="arrow" size={15} /> Download PDF</button>}
          </div>
          {selected.parameters.length > 0 && <div className="user-report-parameters">{selected.parameters.map((parameter) => <label className="data-source-field" key={parameter.name}><span>{parameter.displayName}{parameter.isRequired ? ' *' : ''}</span><input type={parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'} value={parameterValues[parameter.name] ?? ''} onChange={(event) => setParameterValues((current) => ({ ...current, [parameter.name]: event.target.value }))} /></label>)}</div>}
          <button className="button button--primary user-run-report" type="button" onClick={() => void runReport()} disabled={workingId !== null}>{workingId === selected.report.reportId ? <span className="spinner spinner--small" /> : <Icon name="activity" size={16} />} Run report</button>
          {execution && <div className="user-report-print-area">
            <div className="user-report-print-heading"><span>{reportGroup(selected.report.category)} · Enterprise Reporting Studio</span><h1>{selected.design.title || selected.report.name}</h1><p>{selected.report.description}</p></div>
            {execution.charts.map((chart, index) => {
              const data = chartData(execution, chart);
              return <Suspense key={`${chart.type}-${index}`} fallback={<div className="user-chart-loading">Loading chart…</div>}><ReportChartPreview type={chart.type} {...data} /></Suspense>;
            })}
            {selected.design.includeTable !== false && <div className="user-report-result-wrap"><table className="user-report-result"><thead><tr>{execution.fields.map((field) => <th key={field.columnIndex}>{field.displayName}</th>)}</tr></thead><tbody>{execution.rows.map((row, rowIndex) => <tr key={rowIndex}>{row.map((value, cellIndex) => <td key={execution.fields[cellIndex]?.columnIndex}>{value === null ? '' : String(value)}</td>)}</tr>)}</tbody></table></div>}
            {execution.truncated && <p className="user-report-truncated">Preview is limited to 100 rows. Export CSV or Excel for up to 5,000 rows.</p>}
          </div>}
        </section>
      </div>}

          {multiSelected && <div className="user-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) { setMultiSelected(null); setMultiExecution(null); } }}>
            <section className="user-report-modal" role="dialog" aria-modal="true" aria-labelledby="multi-user-report-title">
              <header className="user-report-modal-header"><div><span className="login-kicker">{reportGroup(multiSelected.report.category)} · PUBLISHED v{multiSelected.report.versionNumber} · COMBINED</span><h2 id="multi-user-report-title">{multiSelected.design.title || multiSelected.report.name}</h2></div><button className="icon-button" type="button" aria-label="Close report" onClick={() => { setMultiSelected(null); setMultiExecution(null); }}><Icon name="x" size={18} /></button></header>
              <div className="user-report-modal-actions">
                {multiExecution && <>
                  <button className="button button--secondary" type="button" onClick={() => downloadMultiReport('csv')}><Icon name="arrow" size={15} /> CSV</button>
                  <button className="button button--secondary" type="button" onClick={() => downloadMultiReport('xls')}><Icon name="arrow" size={15} /> Excel</button>
                  <button className="button button--primary" type="button" onClick={() => downloadMultiReport('pdf')}><Icon name="arrow" size={15} /> Download PDF</button>
                </>}
              </div>
              {multiSelected.design.parameters.length > 0 && <div className="user-report-parameters">
                {multiSelected.design.parameters.map((parameter) => <label className="data-source-field" key={parameter.name}>
                  <span>{parameter.displayName}{parameter.isRequired ? ' *' : ''}</span>
                  {parameter.dataType === 'Boolean' ? <select value={multiParameterValues[parameter.name] ?? ''} onChange={(event) => setMultiParameterValues((current) => ({ ...current, [parameter.name]: event.target.value }))}>
                    <option value="">Choose yes or no</option><option value="true">Yes</option><option value="false">No</option>
                  </select> : <input
                    type={parameter.dataType === 'Integer' || parameter.dataType === 'Decimal' ? 'number' : parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                    step={parameter.dataType === 'Decimal' ? 'any' : undefined}
                    value={multiParameterValues[parameter.name] ?? ''}
                    onChange={(event) => setMultiParameterValues((current) => ({ ...current, [parameter.name]: event.target.value }))}
                  />}
                </label>)}
              </div>}
              <button className="button button--primary user-run-report" type="button" onClick={() => void runMultiReport()} disabled={workingId !== null}>
                {workingId === multiSelected.report.reportId ? <span className="spinner spinner--small" /> : <Icon name="activity" size={16} />} Run report
              </button>
              {multiExecution && <div className="user-report-print-area">
                <div className="user-report-print-heading"><span>{reportGroup(multiSelected.report.category)} · Enterprise Reporting Studio</span><h1>{multiSelected.design.title || multiSelected.report.name}</h1><p>{multiSelected.report.description}</p></div>
                {multiExecution.components.map((component) => <section className="multi-report-result" key={component.component.id}>
                  <h3>{component.component.title}</h3>
                  {component.component.type === 'Table' ? <div className="user-report-result-wrap">
                    <SortableReportTable columns={component.columns} rows={component.rows} className="user-report-result" />
                  </div> :
                    <Suspense fallback={<div className="user-chart-loading">Loading chart…</div>}><ReportChartPreview
                      type={component.component.type as 'Bar' | 'Line' | 'Pie'}
                      categoryName={component.columns[0] ?? 'Category'}
                      valueName={component.columns[1] ?? 'Value'}
                      data={component.rows.flatMap((row) => {
                        const value = Number(row[1]);
                        return Number.isFinite(value) ? [{ category: String(row[0] ?? '(blank)'), value }] : [];
                      })}
                    /></Suspense>}
                </section>)}
                {multiExecution.truncated && <p className="user-report-truncated">Combined results were limited for safety. Refine the report or filters and run it again.</p>}
              </div>}
            </section>
          </div>}
    </section>
  );
}
