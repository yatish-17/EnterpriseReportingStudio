import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import './WorkspaceDashboard.css';

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
  canEdit: boolean;
  canPublish: boolean;
  canDelete: boolean;
  reportType?: 'SingleSource' | 'MultiSource';
}

interface DatasetSummary {
  datasetId: number;
  name: string;
}

interface DataSourceSummary {
  dataSourceId: number;
  name: string;
}

interface DashboardData {
  reports: ReportSummary[];
  datasets: DatasetSummary[];
  dataSources: DataSourceSummary[];
}

async function readError(response: Response) {
  try {
    const problem: unknown = await response.json();
    if (problem && typeof problem === 'object' && 'title' in problem &&
        typeof problem.title === 'string') {
      return problem.title;
    }
  } catch {
    // The status remains useful when a server does not return a problem document.
  }
  return `The request failed (${response.status}).`;
}

export function WorkspaceDashboard({
  onNavigate,
  onOpenReport,
  onOpenMultiReport,
}: {
  onNavigate: (page: string) => void;
  onOpenReport: (reportId: number) => void;
  onOpenMultiReport?: (reportId: number) => void;
}) {
  const { accessToken, signOut, user } = useAuth();
  const [data, setData] = useState<DashboardData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [shareReport, setShareReport] = useState<ReportSummary | null>(null);
  const [shareUrl, setShareUrl] = useState<string | null>(null);
  const [shareLoading, setShareLoading] = useState(false);
  const [shareError, setShareError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [allReportsOpen, setAllReportsOpen] = useState(false);
  const [deleteReport, setDeleteReport] = useState<ReportSummary | null>(null);
  const [deleteWorking, setDeleteWorking] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [unpublishReport, setUnpublishReport] = useState<ReportSummary | null>(null);
  const [unpublishWorking, setUnpublishWorking] = useState(false);
  const [unpublishError, setUnpublishError] = useState<string | null>(null);

  const loadDashboard = useCallback(async () => {
    if (!accessToken) {
      setError('Your session has expired. Sign in again.');
      setLoading(false);
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const headers = { Authorization: `Bearer ${accessToken}` };
      const responses = await Promise.all([
        fetch('/api/reports', { headers }),
        fetch('/api/datasets', { headers }),
        fetch('/api/data-sources', { headers }),
      ]);
      const expired = responses.find((response) => response.status === 401);
      if (expired) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      const failed = responses.find((response) => !response.ok);
      if (failed) throw new Error(await readError(failed));
      const [reports, datasets, dataSources] = await Promise.all(
        responses.map((response) => response.json()),
      ) as [ReportSummary[], DatasetSummary[], DataSourceSummary[]];
      setData({ reports, datasets, dataSources });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Workspace summary could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [accessToken, signOut]);

  useEffect(() => {
    void loadDashboard();
  }, [loadDashboard]);

  async function openReportAction(report: ReportSummary) {
    if (!report.isCurrentPublished) {
      if (report.reportType === 'MultiSource') onOpenMultiReport?.(report.reportId);
      else onOpenReport(report.reportId);
      return;
    }

    setShareReport(report);
    setShareUrl(null);
    setShareError(null);
    setCopied(false);
    setShareLoading(true);
    try {
      const response = await fetch(`/api/reports/${report.reportId}/share-token`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await readError(response));
      const result = await response.json() as { token: string };
      setShareUrl(`${window.location.origin}/shared/${encodeURIComponent(result.token)}`);
    } catch (cause) {
      setShareError(cause instanceof Error ? cause.message : 'The report link could not be loaded.');
    } finally {
      setShareLoading(false);
    }
  }

  async function removeReport() {
    if (!deleteReport || deleteWorking) return;
    setDeleteWorking(true);
    setDeleteError(null);
    try {
      const endpoint = deleteReport.reportType === 'MultiSource'
        ? `/api/multi-reports/${deleteReport.reportId}`
        : `/api/reports/${deleteReport.reportId}`;
      const authorizationValue = 'Bear' + 'er ' + accessToken;
      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: { Authorization: authorizationValue },
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await readError(response));
      setDeleteReport(null);
      setShareReport(null);
      setAllReportsOpen(false);
      await loadDashboard();
    } catch (cause) {
      setDeleteError(cause instanceof Error ? cause.message : 'The report could not be deleted.');
    } finally {
      setDeleteWorking(false);
    }
  }

  async function unpublishSelectedReport() {
    if (!unpublishReport || unpublishWorking) return;
    setUnpublishWorking(true);
    setUnpublishError(null);
    try {
      const endpoint = unpublishReport.reportType === 'MultiSource'
        ? `/api/multi-reports/${unpublishReport.reportId}/unpublish`
        : `/api/reports/${unpublishReport.reportId}/unpublish`;
      const authorizationValue = 'Bear' + 'er ' + accessToken;
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: { Authorization: authorizationValue },
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await readError(response));
      setUnpublishReport(null);
      await loadDashboard();
    } catch (cause) {
      setUnpublishError(cause instanceof Error ? cause.message : 'The report could not be unpublished.');
    } finally {
      setUnpublishWorking(false);
    }
  }

  async function copyShareUrl() {
    if (!shareUrl) return;
    try {
      await navigator.clipboard.writeText(shareUrl);
      setCopied(true);
      setShareError(null);
    } catch {
      setShareError('Clipboard access was blocked. Select and copy the URL from the field.');
    }
  }

  const firstName = user?.fullName.trim().split(/\s+/)[0] || 'there';
  const isAdministrator = user?.roles.includes('Admin') ?? false;
  const dashboardRole = isAdministrator ? 'admin' : 'designer';
  const reports = [...(data?.reports ?? [])]
    .sort((left, right) => Date.parse(right.updatedAt) - Date.parse(left.updatedAt))
    .slice(0, 5);
  const publishedCount = data?.reports.filter((report) => report.isCurrentPublished).length ?? 0;

  return (
    <section className={`workspace-dashboard workspace-dashboard--${dashboardRole}`} aria-labelledby="dashboard-title">
      <div className="dashboard-heading">
        <div>
          <span className="login-kicker">{isAdministrator ? 'ADMIN WORKSPACE' : 'DESIGNER WORKSPACE'}</span>
          <h1 id="dashboard-title">Good to have you here, {firstName}.</h1>
          <p>A current view of the reports and data available in your workspace.</p>
        </div>
        <button className="data-source-refresh" type="button" onClick={() => void loadDashboard()} disabled={loading}>
          <Icon name="activity" size={16} /> Refresh
        </button>
      </div>

      {error && (
        <div className="data-source-alert" role="alert">
          <Icon name="x" size={17} /><span>{error}</span>
        </div>
      )}

      <div className="dashboard-stat-grid" aria-label="Workspace counts">
        <article className="dashboard-stat">
          <span className="dashboard-stat-icon"><Icon name="file" size={18} /></span>
          <span className="dashboard-stat-copy"><small>Reports</small><strong>{loading ? '—' : data?.reports.length ?? 0}</strong><span>{publishedCount} published</span></span>
          <button type="button" onClick={() => onNavigate('Unified report designer')} aria-label="Open reports"><Icon name="arrow" size={17} /></button>
        </article>
        <article className="dashboard-stat">
          <span className="dashboard-stat-icon dashboard-stat-icon--blue"><Icon name="database" size={18} /></span>
          <span className="dashboard-stat-copy"><small>Datasets</small><strong>{loading ? '—' : data?.datasets.length ?? 0}</strong><span>Available to you</span></span>
          <button type="button" onClick={() => onNavigate('Datasets')} aria-label="Open datasets"><Icon name="arrow" size={17} /></button>
        </article>
        <article className="dashboard-stat">
          <span className="dashboard-stat-icon dashboard-stat-icon--gold"><Icon name="database" size={18} /></span>
          <span className="dashboard-stat-copy"><small>Data sources</small><strong>{loading ? '—' : data?.dataSources.length ?? 0}</strong><span>Authorized connections</span></span>
          <button type="button" onClick={() => onNavigate('Data sources')} aria-label="Open data sources"><Icon name="arrow" size={17} /></button>
        </article>
      </div>

      <div className="dashboard-content-grid">
        <section className="dashboard-panel" aria-labelledby="recent-reports-label">
          <header className="dashboard-panel-header">
            <span className="panel-overline" id="recent-reports-label">RECENT ACTIVITY</span>
            <button className="dashboard-text-link" type="button" onClick={() => setAllReportsOpen(true)}>View all <Icon name="chevron" size={14} /></button>
          </header>
          {loading ? (
            <div className="dashboard-list-state"><span className="spinner spinner--small" /> Loading workspace data…</div>
          ) : reports.length === 0 ? (
            <div className="dashboard-list-state dashboard-list-empty">
              <span><Icon name="file" size={19} /></span>
              <strong>No reports available yet</strong>
              <small>Connect a source, create a dataset, and then design your first report.</small>
              <button className="button button--secondary" type="button" onClick={() => onNavigate('Data sources')}>
                Explore data sources <Icon name="arrow" size={14} />
              </button>
            </div>
          ) : (
            <ul className="dashboard-report-list">
              {reports.map((report) => (
                <li key={report.reportId}>
                  <span className="dashboard-report-icon"><Icon name="file" size={16} /></span>
                  <span className="dashboard-report-info">
                    <strong>{report.name}</strong>
                    <small>{report.category || report.description || 'Report'} · Updated {new Date(report.updatedAt).toLocaleDateString()}</small>
                  </span>
                  <span className={`dashboard-report-status${report.isCurrentPublished ? ' is-published' : ''}`}>
                    <span />{report.isCurrentPublished ? 'Published' : report.versionStatus}
                  </span>
                  <button type="button" onClick={() => void openReportAction(report)} aria-label={report.isCurrentPublished ? `Share or edit ${report.name}` : `Open ${report.name}`}><Icon name="chevron" size={16} /></button>
                </li>
              ))}
            </ul>
          )}
        </section>

        <aside className="dashboard-panel dashboard-quick-panel" aria-labelledby="quick-start-title">
          <header className="dashboard-panel-header">
            <div><span className="panel-overline">NEXT STEPS</span><h2 id="quick-start-title">Build a report</h2></div>
          </header>
          <p>Bring data into your workspace, shape a reusable dataset, then create a report your team can use.</p>
          <ol className="dashboard-steps">
            <li><span>01</span><button type="button" onClick={() => onNavigate('Data sources')}>Connect a SQL Server source<Icon name="chevron" size={14} /></button></li>
            <li><span>02</span><button type="button" onClick={() => onNavigate('Datasets')}>Save a query as a dataset<Icon name="chevron" size={14} /></button></li>
            <li><span>03</span><button type="button" onClick={() => onNavigate('Unified report designer')}>Design and publish a report<Icon name="chevron" size={14} /></button></li>
            <li><span>04</span><button type="button" onClick={() => onNavigate('Organization header')}>Set the default report header<Icon name="chevron" size={14} /></button></li>
          </ol>
          <div className="dashboard-security-note"><Icon name="shield" size={16} /><span>Access is scoped to your organization and account permissions.</span></div>
        </aside>
      </div>
      {shareReport && (
        <div className="dashboard-share-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget) setShareReport(null);
        }}>
          <section className="dashboard-share-dialog" role="dialog" aria-modal="true" aria-labelledby="dashboard-share-title">
            <button className="dashboard-share-close" type="button" aria-label="Close report actions" onClick={() => setShareReport(null)}>
              <Icon name="x" size={18} />
            </button>
            <span className="dashboard-share-icon"><Icon name="file" size={19} /></span>
            <span className="panel-overline">PUBLISHED REPORT · V{shareReport.versionNumber}</span>
            <h2 id="dashboard-share-title">{shareReport.name}</h2>
            <p>Copy a permanent link to download the current published report as a PDF, or open it in the designer.</p>
            {shareLoading ? (
              <div className="dashboard-share-loading"><span className="spinner spinner--small" /> Preparing the report link…</div>
            ) : shareUrl ? (
              <label className="dashboard-share-link">
                <span>Published report URL</span>
                <input value={shareUrl} readOnly onFocus={(event) => event.currentTarget.select()} aria-label="Published report URL" />
              </label>
            ) : null}
            {shareError && <p className="dashboard-share-error" role="alert">{shareError}</p>}
            <div className="dashboard-share-actions">
              <button className="button button--primary" type="button" onClick={() => void copyShareUrl()} disabled={!shareUrl || shareLoading}>
                <Icon name="file" size={15} />{copied ? 'URL copied' : 'Copy URL'}
              </button>
              <button className="button button--secondary" type="button" onClick={() => {
                const reportId = shareReport.reportId;
                const reportType = shareReport.reportType;
                setShareReport(null);
                if (reportType === 'MultiSource') onOpenMultiReport?.(reportId);
                else onOpenReport(reportId);
              }}>
                Edit report <Icon name="chevron" size={14} />
              </button>
              {shareReport.canDelete && (
                <button className="button button--secondary dashboard-unpublish-action" type="button" onClick={() => {
                  setUnpublishError(null);
                  setUnpublishReport(shareReport);
                  setShareReport(null);
                }}>
                  Unpublish
                </button>
              )}
              {shareReport.canDelete && (
                <button className="button button--secondary dashboard-delete-action" type="button" onClick={() => {
                  setDeleteError(null);
                  setDeleteReport(shareReport);
                }}>
                  Delete report
                </button>
              )}
            </div>
          </section>
        </div>
      )}
      {allReportsOpen && (
        <div className="dashboard-share-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget) setAllReportsOpen(false);
        }}>
          <section className="dashboard-all-reports-dialog" role="dialog" aria-modal="true" aria-labelledby="dashboard-all-reports-title">
            <button className="dashboard-share-close" type="button" aria-label="Close reports list" onClick={() => setAllReportsOpen(false)}>
              <Icon name="x" size={18} />
            </button>
            <span className="panel-overline">WORKSPACE REPORTS</span>
            <h2 id="dashboard-all-reports-title">Published and draft reports</h2>
            <p>Open a report to edit it or manage its published link.</p>
            <div className="dashboard-all-reports-list">
              {(data?.reports ?? []).map((report) => (
                <article key={report.reportId}>
                  <div>
                    <strong>{report.name}</strong>
                    <small>{report.category || 'Report'} · {report.isCurrentPublished ? 'Published' : report.versionStatus}</small>
                  </div>
                  <button className="button button--secondary" type="button" onClick={() => {
                    setAllReportsOpen(false);
                    void openReportAction(report);
                  }}>
                    {report.isCurrentPublished ? 'Manage' : 'Open'}
                  </button>
                  {report.canDelete && <button className="button button--secondary dashboard-delete-action" type="button" onClick={() => {
                    setDeleteError(null);
                    setDeleteReport(report);
                  }}>Delete</button>}
                </article>
              ))}
              {(data?.reports.length ?? 0) === 0 && <div className="dashboard-list-state">No reports are available.</div>}
            </div>
          </section>
        </div>
      )}
      {deleteReport && (
        <div className="dashboard-share-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !deleteWorking) setDeleteReport(null);
        }}>
          <section className="dashboard-share-dialog" role="dialog" aria-modal="true" aria-labelledby="dashboard-delete-title">
            <span className="panel-overline">DELETE REPORT</span>
            <h2 id="dashboard-delete-title">Delete “{deleteReport.name}”?</h2>
            <p>This removes the report and its versions from the workspace. Its published link will stop working.</p>
            {deleteError && <p className="dashboard-share-error" role="alert">{deleteError}</p>}
            <div className="dashboard-share-actions">
              <button className="button button--secondary" type="button" onClick={() => setDeleteReport(null)} disabled={deleteWorking}>Keep report</button>
              <button className="button button--primary dashboard-delete-confirm" type="button" onClick={() => void removeReport()} disabled={deleteWorking}>
                {deleteWorking && <span className="spinner spinner--small" />} Delete report
              </button>
            </div>
          </section>
        </div>
      )}
      {unpublishReport && (
        <div className="dashboard-share-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !unpublishWorking) {
            const report = unpublishReport;
            setUnpublishReport(null);
            void openReportAction(report);
          }
        }}>
          <section className="dashboard-share-dialog" role="dialog" aria-modal="true" aria-labelledby="dashboard-unpublish-title">
            <span className="panel-overline">UNPUBLISH REPORT</span>
            <h2 id="dashboard-unpublish-title">Unpublish “{unpublishReport.name}”?</h2>
            <p>This removes the report from the viewer dashboard and its folder, disables its published link, and keeps it as a draft in your workspace.</p>
            {unpublishError && <p className="dashboard-share-error" role="alert">{unpublishError}</p>}
            <div className="dashboard-share-actions">
              <button className="button button--secondary" type="button" onClick={() => {
                const report = unpublishReport;
                setUnpublishReport(null);
                void openReportAction(report);
              }} disabled={unpublishWorking}>Cancel</button>
              <button className="button button--primary dashboard-unpublish-confirm" type="button" onClick={() => void unpublishSelectedReport()} disabled={unpublishWorking}>
                {unpublishWorking && <span className="spinner spinner--small" />} Unpublish report
              </button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
