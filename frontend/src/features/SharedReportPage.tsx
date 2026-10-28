import { lazy, Suspense, useCallback, useEffect, useState } from 'react';
import { Icon } from '../components/Icon';
import { SortableReportTable } from '../components/SortableReportTable';
import {
  downloadExcelReport,
  downloadReportPdf,
  type ReportDownloadComponent,
} from './reportDownloads';
import { resolveOrganizationReportHeader } from './organizationReportHeader';
import type { ReportPrintHeader } from './reportPrintHeader';
import './SharedReportPage.css';

const ReportChartPreview = lazy(() => import('./ReportChartPreview'));

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
  dataType: 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Date' | 'DateTime';
  defaultValue: string | null;
  isRequired: boolean;
}

interface ReportDetail {
  report: {
    name: string;
    description: string | null;
    category: string | null;
    versionNumber: number;
    publishedBy?: string | null;
    publishedAt?: string | null;
    reportType?: 'SingleSource' | 'MultiSource';
  };
  design: {
    title: string;
    fields: ReportField[];
    chart: ReportChart | null;
    charts?: ReportChart[] | null;
    includeTable?: boolean | null;
    componentOrder?: number[] | null;
    printHeader?: ReportPrintHeader | null;
  };
  parameters: ReportParameter[];
}

interface SharedExecution {
  report: ReportDetail;
  execution: {
    fields: ReportField[];
    rows: (string | number | boolean | null)[][];
    truncated: boolean;
    charts: ReportChart[];
    componentOrder: number[] | null;
  };
}

interface MultiSharedReport {
  report: ReportDetail['report'] & { reportType: 'MultiSource' };
  design: {
    title: string;
    parameters: ReportParameter[];
    printHeader?: ReportPrintHeader | null;
  };
}

interface MultiSharedExecution {
  components: {
    component: { id: string; title: string; type: 'Table' | 'Bar' | 'Line' | 'Pie' };
    columns: string[];
    rows: unknown[][];
  }[];
  truncated: boolean;
  durationMilliseconds: number;
}

function getErrorMessage(response: Response) {
  return response.json().then((problem: unknown) => {
    if (problem && typeof problem === 'object' && 'title' in problem &&
        typeof problem.title === 'string') return problem.title;
    return `The report could not be loaded (${response.status}).`;
  }).catch(() => `The report could not be loaded (${response.status}).`);
}

function parameterValue(value: string, parameter: ReportParameter): string | number | boolean | null {
  if (!value) return null;
  if (parameter.dataType === 'Integer' || parameter.dataType === 'Decimal') return Number(value);
  if (parameter.dataType === 'Boolean') return value === 'true';
  return value;
}

function safeFileName(value: string) {
  return value.replace(/[<>:"/\\|?*]/g, '_').trim() || 'report';
}

async function downloadPdf(document: SharedExecution, values: Record<string, string>) {
  const { report, execution } = document;
  const charts = execution.charts?.length
    ? execution.charts
    : report.design.charts?.length ? report.design.charts : report.design.chart ? [report.design.chart] : [];
  const includeTable = report.design.includeTable ?? charts.length === 0;
  const order = report.design.componentOrder?.length
    ? report.design.componentOrder
    : [...(includeTable ? [-1] : []), ...charts.map((_, index) => index)];
  const components: ReportDownloadComponent[] = [];
  for (const componentId of order) {
    if (componentId === -1 && includeTable) {
      components.push({
        title: 'Details',
        type: 'Table',
        columns: execution.fields.map((field) => field.displayName),
        rows: execution.rows,
      });
      continue;
    }

    const chart = charts[componentId];
    if (!chart) continue;
    const categoryIndex = execution.fields.findIndex((field) =>
      field.columnIndex === chart.categoryColumnIndex);
    const valueIndex = execution.fields.findIndex((field) =>
      field.columnIndex === chart.valueColumnIndex);
    components.push({
      title: `${chart.type} chart`,
      type: chart.type,
      columns: ['Category', 'Value'],
      rows: execution.rows.map((row) => [row[categoryIndex], row[valueIndex]]),
    });
  }
  await downloadReportPdf(
    safeFileName(report.report.name),
    report.design.title || report.report.name,
    report.report.category || 'Published',
    components,
    execution.truncated,
    report.design.printHeader,
    {
      parameters: report.parameters.map((parameter) => ({
        name: parameter.displayName,
        value: parameterValue(values[parameter.name] ?? parameter.defaultValue ?? '', parameter),
      })),
      publishedBy: report.report.publishedBy,
      publishedAt: report.report.publishedAt,
      versionNumber: report.report.versionNumber,
    },
  );
}

export function SharedReportPage({ token }: { token: string }) {
  const [report, setReport] = useState<ReportDetail | null>(null);
  const [multiReport, setMultiReport] = useState<MultiSharedReport | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(true);
  const [downloaded, setDownloaded] = useState(false);
  const [missingRequired, setMissingRequired] = useState(false);
  const [multiExecution, setMultiExecution] = useState<MultiSharedExecution | null>(null);

  useEffect(() => {
    let active = true;
    void fetch(`/api/reports/share/${encodeURIComponent(token)}`)
      .then(async (response) => {
        if (!response.ok) throw new Error(await getErrorMessage(response));
        return response.json() as Promise<ReportDetail | MultiSharedReport>;
      })
      .then(async (response) => {
        if (!active) return;
        if (response.report.reportType === 'MultiSource') {
          const shared = response as MultiSharedReport;
          const detail: MultiSharedReport = {
            ...shared,
            design: {
              ...shared.design,
              printHeader: await resolveOrganizationReportHeader(shared.design.printHeader),
            },
          };
          if (!active) return;
          setMultiReport(detail);
          setValues(Object.fromEntries(detail.design.parameters.map((parameter) => [
            parameter.name,
            parameter.defaultValue ?? '',
          ])));
          setMissingRequired(detail.design.parameters.some((parameter) => parameter.isRequired && !parameter.defaultValue));
          setBusy(false);
          return;
        }
        const shared = response as ReportDetail;
        const detail: ReportDetail = {
          ...shared,
          design: {
            ...shared.design,
            printHeader: await resolveOrganizationReportHeader(shared.design.printHeader),
          },
        };
        if (!active) return;
        setReport(detail);
        setValues(Object.fromEntries(detail.parameters.map((parameter) => [
          parameter.name,
          parameter.defaultValue ?? '',
        ])));
        setMissingRequired(detail.parameters.some((parameter) => parameter.isRequired && !parameter.defaultValue));
        setBusy(false);
      })
      .catch((cause: unknown) => {
        if (active) {
          setError(cause instanceof Error ? cause.message : 'This shared report is unavailable.');
          setBusy(false);
        }
      });
    return () => { active = false; };
  }, [token]);

  const runMultiReport = useCallback(async () => {
    if (!multiReport) return;
    setBusy(true);
    setError(null);
    setMultiExecution(null);
    try {
      const parameters = Object.fromEntries(multiReport.design.parameters.map((parameter) => [
        parameter.name,
        parameterValue(values[parameter.name] ?? parameter.defaultValue ?? '', parameter),
      ]));
      const response = await fetch(`/api/reports/share/${encodeURIComponent(token)}/execute`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ parameters }),
      });
      if (!response.ok) throw new Error(await getErrorMessage(response));
      const result = await response.json() as MultiSharedExecution;
      setMultiExecution(result);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be run.');
    } finally {
      setBusy(false);
    }
  }, [multiReport, token, values]);

  const executeSingleReport = useCallback(async () => {
    if (!report) throw new Error('The report is not ready to run.');
    const parameters = Object.fromEntries(report.parameters.map((parameter) => [
      parameter.name,
      parameterValue(values[parameter.name] ?? parameter.defaultValue ?? '', parameter),
    ]));
    const response = await fetch(`/api/reports/share/${encodeURIComponent(token)}/execute`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ parameters }),
    });
    if (!response.ok) throw new Error(await getErrorMessage(response));
    const result = await response.json() as SharedExecution;
    return result;
  }, [report, token, values]);

  const createPdf = useCallback(async () => {
    if (!report) return;
    setBusy(true);
    setError(null);
    try {
      const result = await executeSingleReport();
      await downloadPdf(result, values);
      setDownloaded(true);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The PDF could not be generated.');
    } finally {
      setBusy(false);
    }
  }, [executeSingleReport, report, values]);

  async function downloadExcel() {
    if (report) {
      setBusy(true);
      setError(null);
      try {
        const result = await executeSingleReport();
        downloadExcelReport(report.report.name, [{
          title: report.design.title || report.report.name,
          type: 'Table',
          columns: result.execution.fields.map((field) => field.displayName),
          rows: result.execution.rows,
        }], report.design.printHeader);
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'The Excel download could not be created.');
      } finally {
        setBusy(false);
      }
      return;
    }
    if (multiReport && multiExecution) {
      downloadExcelReport(multiReport.report.name, multiExecution.components.map((component) => ({
        title: component.component.title,
        type: component.component.type,
        columns: component.columns,
        rows: component.rows,
      })), multiReport.design.printHeader);
    }
  }

  async function downloadMultiPdf() {
    if (!multiReport || !multiExecution) return;
    setError(null);
    setBusy(true);
    try {
      await downloadReportPdf(
        multiReport.report.name,
        multiReport.design.title || multiReport.report.name,
        multiReport.report.category || 'Published',
        multiExecution.components.map((component) => ({
          title: component.component.title,
          type: component.component.type,
          columns: component.columns,
          rows: component.rows,
        })),
        multiExecution.truncated,
        multiReport.design.printHeader,
        {
          parameters: multiReport.design.parameters.map((parameter) => ({
            name: parameter.displayName,
            value: parameterValue(values[parameter.name] ?? parameter.defaultValue ?? '', parameter),
          })),
          publishedBy: multiReport.report.publishedBy,
          publishedAt: multiReport.report.publishedAt,
          versionNumber: multiReport.report.versionNumber,
        },
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The PDF could not be generated.');
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    if (report && !missingRequired && !downloaded && !error) void createPdf();
  }, [createPdf, downloaded, error, missingRequired, report]);

  useEffect(() => {
    if (multiReport && !missingRequired && !multiExecution && !busy && !error) void runMultiReport();
  }, [busy, error, missingRequired, multiExecution, multiReport, runMultiReport]);

  const activeReport = multiReport ?? report;
  const reportTitle = multiReport?.design.title || report?.design.title || activeReport?.report.name;
  const reportParameters = multiReport?.design.parameters ?? report?.parameters ?? [];

  return (
    <main className="shared-report-page">
      <div className="shared-report-brand"><span className="shared-report-mark"><i /><i /><i /></span><strong>Enterprise <small>REPORTING STUDIO</small></strong></div>
      <section className={`shared-report-card${multiReport ? ' shared-report-card--multi' : ''}`}>
        <span className="shared-report-kicker">SECURE REPORT LINK</span>
        {busy ? (
          <><span className="spinner spinner--large" /><h1>{multiReport ? 'Running your report' : 'Preparing your report'}</h1><p>{multiReport ? 'Loading the published tables and charts.' : 'Your published report is being rendered as a PDF.'}</p></>
        ) : error ? (
          <><span className="shared-report-icon shared-report-icon--error"><Icon name="x" size={20} /></span><h1>Report unavailable</h1><p>{error}</p></>
        ) : activeReport ? (
          <>
            <span className="shared-report-icon"><Icon name="file" size={20} /></span>
            <h1>{reportTitle}</h1>
            <p>{activeReport.report.description || 'Published report'} · Version {activeReport.report.versionNumber}</p>
            {multiReport && (
              <>
                {reportParameters.length > 0 && (
                  <div className="shared-report-parameters">
                    <p>Set the report values, then run the report.</p>
                    {reportParameters.map((parameter) => (
                      <label key={parameter.name}>
                        <span>{parameter.displayName}{parameter.isRequired ? ' *' : ''}</span>
                        {parameter.dataType === 'Boolean' ? (
                          <select value={values[parameter.name] ?? ''} onChange={(event) => setValues((current) => ({ ...current, [parameter.name]: event.target.value }))}>
                            <option value="">Choose yes or no</option><option value="true">Yes</option><option value="false">No</option>
                          </select>
                        ) : (
                          <input
                            type={parameter.dataType === 'Integer' || parameter.dataType === 'Decimal' ? 'number' : parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                            step={parameter.dataType === 'Decimal' ? 'any' : undefined}
                            required={parameter.isRequired}
                            value={values[parameter.name] ?? ''}
                            onChange={(event) => setValues((current) => ({ ...current, [parameter.name]: event.target.value }))}
                          />
                        )}
                      </label>
                    ))}
                  </div>
                )}
                <button className="button button--primary" type="button" onClick={() => void runMultiReport()} disabled={busy || reportParameters.some((parameter) => parameter.isRequired && !(values[parameter.name] ?? '').trim())}>
                  Run report
                </button>
                {multiExecution && (
                  <div className="shared-report-results">
                    <p className="shared-report-results-meta">{multiExecution.durationMilliseconds} ms · {multiExecution.truncated ? 'Results limited for preview.' : 'Report complete.'}</p>
                    <div className="shared-report-download-actions">
                      <button className="button button--secondary" type="button" onClick={() => void downloadExcel()} disabled={busy}><Icon name="arrow" size={15} /> Download Excel</button>
                      <button className="button button--primary" type="button" onClick={() => void downloadMultiPdf()} disabled={busy}><Icon name="arrow" size={15} /> Download PDF</button>
                    </div>
                    {multiExecution.components.map((component) => (
                      <section className="shared-report-result" key={component.component.id}>
                        <h2>{component.component.title}</h2>
                        {component.component.type === 'Table' ? (
                          <div className="shared-report-table-wrap">
                            <SortableReportTable columns={component.columns} rows={component.rows} className="shared-report-table" />
                          </div>
                        ) : (
                          <Suspense fallback={<div className="user-chart-loading">Loading chart…</div>}>
                            <ReportChartPreview
                              type={component.component.type}
                              categoryName={component.columns[0] ?? 'Category'}
                              valueName={component.columns[1] ?? 'Value'}
                              data={component.rows.flatMap((row) => {
                                const value = Number(row[1]);
                                return Number.isFinite(value) ? [{ category: String(row[0] ?? '(blank)'), value }] : [];
                              })}
                            />
                          </Suspense>
                        )}
                      </section>
                    ))}
                  </div>
                )}
              </>
            )}
            {!multiReport && missingRequired && (
              <div className="shared-report-parameters">
                <p>Enter the required report values, then choose a download format.</p>
                {reportParameters.map((parameter) => (
                  <label key={parameter.name}>
                    <span>{parameter.displayName}{parameter.isRequired ? ' *' : ''}</span>
                    {parameter.dataType === 'Boolean' ? (
                      <select value={values[parameter.name] ?? ''} onChange={(event) => setValues((current) => ({ ...current, [parameter.name]: event.target.value }))}>
                        <option value="">Choose yes or no</option><option value="true">Yes</option><option value="false">No</option>
                      </select>
                    ) : (
                      <input
                        type={parameter.dataType === 'Integer' || parameter.dataType === 'Decimal' ? 'number' : parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                        step={parameter.dataType === 'Decimal' ? 'any' : undefined}
                        required={parameter.isRequired}
                        value={values[parameter.name] ?? ''}
                        onChange={(event) => setValues((current) => ({ ...current, [parameter.name]: event.target.value }))}
                      />
                    )}
                  </label>
                ))}
                <div className="shared-report-download-actions">
                  <button className="button button--secondary" type="button" onClick={() => void downloadExcel()} disabled={busy || reportParameters.some((parameter) => parameter.isRequired && !(values[parameter.name] ?? '').trim())}><Icon name="arrow" size={15} /> Download Excel</button>
                  <button className="button button--primary" type="button" onClick={() => void createPdf()} disabled={busy || reportParameters.some((parameter) => parameter.isRequired && !(values[parameter.name] ?? '').trim())}><Icon name="arrow" size={15} /> Download PDF</button>
                </div>
              </div>
            )}
            {!multiReport && downloaded && <><p className="shared-report-success">Your PDF is ready. Choose a format to download the report again.</p><div className="shared-report-download-actions"><button className="button button--secondary" type="button" onClick={() => void downloadExcel()} disabled={busy}><Icon name="arrow" size={15} /> Download Excel</button><button className="button button--primary" type="button" onClick={() => void createPdf()} disabled={busy}>Download PDF</button></div></>}
          </>
        ) : null}
      </section>
      <footer>Shared by your organization · Only the current published version is available.</footer>
    </main>
  );
}
