import { lazy, Suspense, useState, type FormEvent } from 'react';
import { AuthenticationError, useAuth } from './auth/AuthProvider';
import { Icon, type IconName } from './components/Icon';
import { DataSourcesPage } from './features/DataSourcesPage';
import { DatasetsPage } from './features/DatasetsPage';
import { DataManagementPage } from './features/DataManagementPage';
import { ReportsPage } from './features/ReportsPage';
import { MultiSourceReportsPage } from './features/MultiSourceReportsPage';
import { TeamAccessPage } from './features/TeamAccessPage';
import { UserReportsPage } from './features/UserReportsPage';
import { WorkspaceDashboard } from './features/WorkspaceDashboard';
import { OrganizationHeaderTemplatePage } from './features/OrganizationHeaderTemplatePage';
import './App.css';

const SharedReportPage = lazy(() =>
  import('./features/SharedReportPage').then((module) => ({ default: module.SharedReportPage })),
);

interface NavigationItem {
  label: string;
  icon: IconName;
  active?: boolean;
  available: boolean;
}

const adminNavigation: NavigationItem[] = [
  { label: 'Workspace', icon: 'grid', available: true },
  { label: 'Data management', icon: 'database', available: true },
  { label: 'Unified report designer', icon: 'activity', available: true },
  { label: 'Team & access', icon: 'users', available: true },
];

const designerNavigation: NavigationItem[] = [
  { label: 'Workspace', icon: 'grid', available: true },
  { label: 'Data management', icon: 'database', available: true },
  { label: 'Unified report designer', icon: 'activity', available: true },
];

const viewerNavigation: NavigationItem[] = [
  { label: 'Published reports', icon: 'file', available: true },
];

function Brand({ compact = false }: { compact?: boolean }) {
  return (
    <div className={`brand${compact ? ' brand--compact' : ''}`}>
      <span className="brand-mark" aria-hidden="true">
        <span />
        <span />
        <span />
      </span>
      <span className="brand-name">
        <strong>Enterprise</strong>
        <small>REPORTING STUDIO</small>
      </span>
    </div>
  );
}

function LoginScreen() {
  const { signIn, notice, clearNotice, status } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setFieldError(null);
    clearNotice();

    if (!email.trim() || !password) {
      setFieldError('Enter your work email and password to continue.');
      return;
    }

    setSubmitting(true);
    try {
      await signIn(email.trim(), password);
    } catch (cause) {
      if (cause instanceof AuthenticationError) {
        if (cause.failure === 'credentials') {
          setError('We could not sign you in with those details. Check your email and password.');
        } else if (cause.failure === 'unavailable') {
          setError('The sign-in service could not be reached. Check your connection and try again.');
        } else {
          setError('The sign-in service returned an unexpected response. Please try again later.');
        }
      } else {
        setError('We could not complete sign-in. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main className="login-page">
      <section className="login-panel" aria-label="Sign in">
        <div className="login-brand"><Brand /></div>
        <div className="login-content">
          <div className="login-heading">
            <span className="login-kicker">YOUR WORKSPACE AWAITS</span>
            <h2>Welcome back</h2>
            <p>Sign in with your organization credentials to continue.</p>
          </div>

          {notice && (
            <div className="notice notice--info" role="status">
              <Icon name="shield" size={17} />
              <span>{notice}</span>
              <button className="notice-dismiss" type="button" aria-label="Dismiss notification" onClick={clearNotice}>
                <Icon name="x" size={16} />
              </button>
            </div>
          )}
          {status === 'unavailable' && !error && (
            <div className="notice notice--warning" role="alert">
              <Icon name="activity" size={17} />
              <span>We could not verify your saved session. Sign in again when the service is available.</span>
            </div>
          )}
          {error && (
            <div className="notice notice--error" role="alert">
              <Icon name="x" size={17} />
              <span>{error}</span>
            </div>
          )}

          <form className="login-form" onSubmit={handleSubmit} noValidate>
            <div className="field">
              <label htmlFor="email">Work email</label>
              <div className="input-wrap">
                <span className="input-icon"><Icon name="users" size={18} /></span>
                <input
                  id="email"
                  name="email"
                  type="email"
                  autoComplete="username"
                  placeholder="you@company.com"
                  value={email}
                  onChange={(event) => { setEmail(event.target.value); setFieldError(null); }}
                  disabled={submitting}
                  required
                />
              </div>
            </div>

            <div className="field">
              <div className="field-label-row">
                <label htmlFor="password">Password</label>
              </div>
              <div className="input-wrap">
                <span className="input-icon"><Icon name="lock" size={18} /></span>
                <input
                  id="password"
                  name="password"
                  type={passwordVisible ? 'text' : 'password'}
                  autoComplete="current-password"
                  placeholder="Enter password"
                  value={password}
                  onChange={(event) => { setPassword(event.target.value); setFieldError(null); }}
                  disabled={submitting}
                  required
                />
                <button
                  className="password-toggle"
                  type="button"
                  aria-label={passwordVisible ? 'Hide password' : 'Show password'}
                  aria-pressed={passwordVisible}
                  onClick={() => setPasswordVisible((visible) => !visible)}
                  disabled={submitting}
                >
                  <Icon name={passwordVisible ? 'eyeOff' : 'eye'} size={18} />
                </button>
              </div>
            </div>

            {fieldError && <p className="field-error" role="alert">{fieldError}</p>}

            <button className="button button--primary login-submit" type="submit" disabled={submitting}>
              {submitting ? (
                <><span className="spinner" aria-hidden="true" /> Verifying your account</>
              ) : (
                <>Sign in securely <Icon name="arrow" size={18} /></>
              )}
            </button>
          </form>

          <div className="login-security">
            <Icon name="lock" size={15} />
            <span>Your access token expires automatically after 15 minutes.</span>
          </div>
        </div>
        <footer className="login-legal">
          <span>© {new Date().getFullYear()} Enterprise Reporting Studio</span>
          <span className="legal-separator" />
          <span>Secure organization access</span>
        </footer>
      </section>
    </main>
  );
}

function initials(name: string) {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('');
}

function WorkspaceShell() {
  const { user, signOut } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const [page, setPage] = useState('Workspace');
  const [requestedReportId, setRequestedReportId] = useState<number | null>(null);
  const [requestedUnifiedReportId, setRequestedUnifiedReportId] = useState<number | null>(null);
  const [requestedDatasetId, setRequestedDatasetId] = useState<number | null>(null);
  if (!user) return null;

  const isAdmin = user.roles.includes('Admin');
  const isDesigner = user.roles.includes('Report Designer');
  const isViewer = user.roles.includes('Report Viewer');
  const navigation = isAdmin
    ? adminNavigation
    : isDesigner
      ? designerNavigation
      : isViewer
        ? viewerNavigation
        : [];
  const roleLabel = isAdmin ? 'Administrator' : isDesigner ? 'Designer' : 'User';

  async function handleSignOut() {
    setSigningOut(true);
    await signOut();
  }

  function openWorkspaceReport(reportId: number) {
    setRequestedReportId(reportId);
    setPage('Unified report designer');
  }

  function openUnifiedReport(reportId: number | null = null) {
    setRequestedUnifiedReportId(reportId);
    setRequestedDatasetId(null);
    setPage('Unified report editor');
    setMenuOpen(false);
  }

  function designDataset(datasetId: number) {
    setRequestedUnifiedReportId(null);
    setRequestedDatasetId(datasetId);
    setPage('Unified report editor');
    setMenuOpen(false);
  }

  const pageTitle = page === 'Unified report editor' ? 'Unified report designer' : page;

  return (
    <div className="workspace">
      {menuOpen && <button className="sidebar-scrim" aria-label="Close navigation" onClick={() => setMenuOpen(false)} />}
      <aside className={`sidebar${menuOpen ? ' sidebar--open' : ''}`}>
        <div className="sidebar-brand"><Brand /></div>
        {navigation.length > 0 && <nav className="sidebar-nav" aria-label={`${roleLabel} navigation`}>
          {isViewer ? null : <span className="nav-section-label">WORKSPACE</span>}
          {navigation.map((item) => (
            <button
              key={item.label}
              className={`nav-item${(page === item.label || (item.label === 'Unified report designer' && page === 'Unified report editor') || (item.label === 'Data management' && (page === 'Data sources' || page === 'Datasets'))) ? ' nav-item--active' : ''}${!item.available ? ' nav-item--disabled' : ''}`}
              type="button"
              disabled={!item.available}
              title={item.available ? item.label : `${item.label} will be available in a future module`}
              onClick={() => { setPage(item.label); setMenuOpen(false); }}
              aria-current={(page === item.label || (item.label === 'Unified report designer' && page === 'Unified report editor') || (item.label === 'Data management' && (page === 'Data sources' || page === 'Datasets'))) ? 'page' : undefined}
            >
              <Icon name={item.icon} size={19} />
              <span>{item.label}</span>
              {!item.available && <span className="nav-soon">SOON</span>}
            </button>
          ))}
        </nav>}
        <div className="sidebar-bottom">
          <div className="sidebar-security">
            <span className="security-icon"><Icon name="shield" size={17} /></span>
            <span><strong>Protected workspace</strong><small>Access verified for your role</small></span>
          </div>
          <div className="sidebar-version">Enterprise <span>·</span> REPORTING STUDIO</div>
        </div>
      </aside>

      <div className="workspace-main">
        <header className="topbar">
          <div className="topbar-leading">
            <button className="icon-button mobile-menu-button" type="button" aria-label="Open navigation" onClick={() => setMenuOpen(true)}>
              <Icon name="menu" size={20} />
            </button>
            <div className="breadcrumbs"><span>Workspace</span><Icon name="chevron" size={15} /><strong>{pageTitle}</strong></div>
          </div>
          <div className="topbar-account">
            <span className="role-chip"><span />{roleLabel}</span>
            <span className="topbar-divider" />
            <div className="profile-summary">
              <span className="profile-avatar">{initials(user.fullName)}</span>
              <span className="profile-copy"><strong>{user.fullName}</strong><small>{user.email}</small></span>
            </div>
            <button className="icon-button logout-button" type="button" onClick={handleSignOut} disabled={signingOut} aria-label="Sign out" title="Sign out">
              {signingOut ? <span className="spinner spinner--small" /> : <Icon name="logout" size={19} />}
            </button>
          </div>
        </header>

        <main className={`workspace-content${isViewer ? ' workspace-content--catalog' : ''}`}>
          {isViewer ? (
              <UserReportsPage />
            ) : page === 'Organization header' && (isAdmin || isDesigner) ? (
              <OrganizationHeaderTemplatePage onBack={() => setPage('Workspace')} />
            ) : page === 'Team & access' && isAdmin ? (
              <TeamAccessPage />
            ) : page === 'Data management' && (isAdmin || isDesigner) ? (
              <DataManagementPage
                onOpenDataSources={() => setPage('Data sources')}
                onOpenDatasets={() => setPage('Datasets')}
              />
            ) : page === 'Data sources' && (isAdmin || isDesigner) ? (
              <div className="data-management-subpage">
                <button className="data-management-back" type="button" onClick={() => setPage('Data management')}>
                  <Icon name="chevron" size={16} /> Back to Data management
                </button>
                <DataSourcesPage canManage={isAdmin || isDesigner} />
              </div>
            ) : page === 'Datasets' && (isAdmin || isDesigner) ? (
              <div className="data-management-subpage">
                <button className="data-management-back" type="button" onClick={() => setPage('Data management')}>
                  <Icon name="chevron" size={16} /> Back to Data management
                </button>
                <DatasetsPage onDesignDataset={designDataset} />
              </div>
            ) : page === 'Unified report editor' && (isAdmin || isDesigner) ? (
              <MultiSourceReportsPage
                key={requestedUnifiedReportId
                  ? `report-${requestedUnifiedReportId}`
                  : requestedDatasetId
                    ? `dataset-${requestedDatasetId}`
                    : 'new'}
                initialReportId={requestedUnifiedReportId}
                initialDatasetId={requestedDatasetId}
                onBack={() => {
                  setRequestedUnifiedReportId(null);
                  setRequestedDatasetId(null);
                  setPage('Unified report designer');
                }}
              />
            ) : (page === 'Unified report designer' || page === 'My reports') && (isAdmin || isDesigner) ? (
              <ReportsPage
                initialReportId={requestedReportId}
                onInitialReportHandled={() => setRequestedReportId(null)}
                onCreateUnifiedReport={() => openUnifiedReport()}
                onOpenUnifiedReport={openUnifiedReport}
              />
            ) : (
              <WorkspaceDashboard
                onNavigate={setPage}
                onOpenReport={openWorkspaceReport}
                onOpenMultiReport={openUnifiedReport}
              />
          )}
        </main>
        <footer className="workspace-footer"><span>Enterprise Reporting Studio</span><span>Private, organization-scoped reporting</span></footer>
      </div>

    </div>
  );
}

export default function App() {
  const sharedReportToken = /^\/shared\/([^/]+)\/?$/.exec(window.location.pathname)?.[1];
  if (sharedReportToken) {
    return (
      <Suspense fallback={<main className="app-loading"><span className="spinner spinner--large" /><span>Preparing your report…</span></main>}>
        <SharedReportPage token={decodeURIComponent(sharedReportToken)} />
      </Suspense>
    );
  }

  const { status } = useAuth();
  if (status === 'loading') {
    return <main className="app-loading"><span className="spinner spinner--large" /><span>Securing your workspace…</span></main>;
  }
  if (status === 'authenticated') return <WorkspaceShell />;
  return <LoginScreen />;
}
