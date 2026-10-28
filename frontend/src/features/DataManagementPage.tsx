import { Icon } from '../components/Icon';
import './DataManagementPage.css';

interface DataManagementPageProps {
  onOpenDataSources: () => void;
  onOpenDatasets: () => void;
}

const sections = [
  {
    title: 'Data sources',
    description: 'Connect and manage the SQL Server databases your organization can use.',
    icon: 'database' as const,
    open: 'onOpenDataSources' as const,
  },
  {
    title: 'Datasets',
    description: 'Create reusable queries and inspect the data available for reports.',
    icon: 'file' as const,
    open: 'onOpenDatasets' as const,
  },
];

export function DataManagementPage({ onOpenDataSources, onOpenDatasets }: DataManagementPageProps) {
  const actions = { onOpenDataSources, onOpenDatasets };

  return (
    <section className="data-management-page" aria-labelledby="data-management-title">
      <header className="data-management-heading">
        <span className="login-kicker">DATA WORKSPACE</span>
        <h1 id="data-management-title">Your data, organized.</h1>
        <p>Manage database connections and the reusable datasets that power your reports.</p>
      </header>
      <div className="data-management-folders" aria-label="Data workspace folders">
        {sections.map((section) => (
          <button
            className="data-management-folder"
            key={section.title}
            type="button"
            onClick={actions[section.open]}
          >
            <span className="data-management-folder-icon"><Icon name={section.icon} size={23} /></span>
            <span className="data-management-folder-copy">
              <strong>{section.title}</strong>
              <small>{section.description}</small>
            </span>
            <Icon className="data-management-folder-arrow" name="chevron" size={18} />
          </button>
        ))}
      </div>
    </section>
  );
}
