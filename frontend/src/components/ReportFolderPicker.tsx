import { useState } from 'react';
import { Icon } from './Icon';
import './ReportFolderPicker.css';

export interface ReportFolder {
  name: string;
  reportCount: number;
}

export function ReportFolderPicker({
  folders,
  value,
  onChange,
  onCreate,
  onDelete,
  disabled = false,
}: {
  folders: ReportFolder[];
  value: string;
  onChange: (name: string) => void;
  onCreate: (name: string) => Promise<void>;
  onDelete: (folder: ReportFolder) => Promise<void>;
  disabled?: boolean;
}) {
  const [newName, setNewName] = useState('');
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function createFolder() {
    const name = newName.trim();
    if (!name || working) return;
    setWorking(true);
    setError(null);
    try {
      await onCreate(name);
      setNewName('');
      onChange(name);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The folder could not be created.');
    } finally {
      setWorking(false);
    }
  }

  async function removeFolder(folder: ReportFolder) {
    if (folder.reportCount > 0 || working) return;
    setWorking(true);
    setError(null);
    try {
      await onDelete(folder);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The folder could not be deleted.');
    } finally {
      setWorking(false);
    }
  }

  return (
    <div className="report-folder-picker">
      <div className="report-folder-options" role="radiogroup" aria-label="Dashboard folder">
        {folders.map((folder) => (
          <div
            className={`report-folder-option${value === folder.name ? ' is-selected' : ''}`}
            key={folder.name}
          >
            <label>
              <input
                type="radio"
                name="publish-folder"
                value={folder.name}
                checked={value === folder.name}
                onChange={() => onChange(folder.name)}
                disabled={disabled || working}
              />
              <span>{folder.name}</span>
              <small>{folder.reportCount}</small>
            </label>
            <button
              className="report-folder-delete"
              type="button"
              aria-label={`Delete empty ${folder.name} folder`}
              title={folder.reportCount === 0 ? 'Delete empty folder' : 'Delete all reports in this folder first'}
              onClick={() => void removeFolder(folder)}
              disabled={disabled || working || folder.reportCount > 0}
            >
              <Icon name="x" size={15} />
            </button>
          </div>
        ))}
      </div>
      <form className="report-folder-create" onSubmit={(event) => {
        event.preventDefault();
        void createFolder();
      }}>
        <input
          value={newName}
          maxLength={150}
          aria-label="New folder name"
          placeholder="New folder name"
          onChange={(event) => setNewName(event.target.value)}
          disabled={disabled || working}
        />
        <button
          className="button button--secondary"
          type="submit"
          disabled={disabled || working || !newName.trim()}
        >
          <Icon name="plus" size={14} /> Create folder
        </button>
      </form>
      {error && <p className="report-folder-picker-error" role="alert">{error}</p>}
    </div>
  );
}
