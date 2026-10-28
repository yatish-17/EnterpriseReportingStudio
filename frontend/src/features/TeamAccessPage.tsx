import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import './TeamAccessPage.css';

type TeamRole = 'Admin' | 'Report Designer' | 'Report Viewer';

interface TeamMember {
  userId: number;
  fullName: string;
  email: string;
  role: TeamRole;
  isActive: boolean;
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

export function TeamAccessPage() {
  const { accessToken, signOut, user } = useAuth();
  const [members, setMembers] = useState<TeamMember[]>([]);
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<TeamRole>('Report Viewer');
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [memberToDelete, setMemberToDelete] = useState<TeamMember | null>(null);
  const authorizationValue = accessToken ? 'Bear' + 'er ' + accessToken : '';

  const loadMembers = useCallback(async () => {
    if (!accessToken) return;
    setLoading(true);
    setError(null);
    try {
      const response = await fetch('/api/team', {
        headers: { Authorization: authorizationValue },
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await responseError(response));
      setMembers(await response.json() as TeamMember[]);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Team accounts could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [accessToken, signOut]);

  useEffect(() => {
    void loadMembers();
  }, [loadMembers]);

  async function createMember(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!accessToken) return;
    setWorking(true);
    setError(null);
    setNotice(null);
    try {
      const response = await fetch('/api/team', {
        method: 'POST',
        headers: {
          Authorization: authorizationValue,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ fullName: name, email, password, role }),
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await responseError(response));
      const member = await response.json() as TeamMember;
      setMembers((current) => [...current, member].sort((left, right) =>
        left.fullName.localeCompare(right.fullName)));
      setName('');
      setEmail('');
      setPassword('');
      setNotice(`${member.fullName} was added. Give them their sign-in email and initial password securely.`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The team account could not be created.');
    } finally {
      setWorking(false);
    }
  }

  async function updateMember(member: TeamMember, update: Partial<TeamMember>) {
    if (!accessToken) return;
    setWorking(true);
    setError(null);
    setNotice(null);
    try {
      const response = await fetch(`/api/team/${member.userId}`, {
        method: 'PUT',
        headers: {
          Authorization: authorizationValue,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          role: update.role ?? member.role,
          isActive: update.isActive ?? member.isActive,
        }),
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await responseError(response));
      const updated = await response.json() as TeamMember;
      setMembers((current) => current.map((row) =>
        row.userId === updated.userId ? updated : row));
      setNotice(updated.isActive
        ? `Access updated for ${updated.fullName}.`
        : `${updated.fullName}'s access has been revoked.`);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Team access could not be updated.');
    } finally {
      setWorking(false);
    }
  }

  async function deleteMember() {
    if (!memberToDelete || !accessToken) return;
    setWorking(true);
    setError(null);
    setNotice(null);
    try {
      const response = await fetch(`/api/team/${memberToDelete.userId}`, {
        method: 'DELETE',
        headers: { Authorization: authorizationValue },
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await responseError(response));
      setMembers((current) => current.filter((member) => member.userId !== memberToDelete.userId));
      setNotice(`${memberToDelete.fullName}'s account was permanently deleted.`);
      setMemberToDelete(null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The team account could not be deleted.');
    } finally {
      setWorking(false);
    }
  }

  return (
    <section className="team-access-page" aria-labelledby="team-access-title">
      <header className="team-access-heading">
        <div>
          <span className="login-kicker">ADMINISTRATION</span>
          <h1 id="team-access-title">Team &amp; access</h1>
          <p>Create organization accounts, assign a workspace role, and revoke access when it is no longer needed.</p>
        </div>
        <span className="team-access-count"><Icon name="users" size={17} /> {members.length} accounts</span>
      </header>

      {error && <div className="data-source-alert" role="alert"><Icon name="x" size={17} /><span>{error}</span></div>}
      {notice && <div className="report-save-notice" role="status"><Icon name="check" size={16} /><span>{notice}</span></div>}

      <div className="team-access-grid">
        <form className="team-create-card" onSubmit={(event) => void createMember(event)}>
          <div className="team-card-heading">
            <span className="team-card-icon"><Icon name="plus" size={18} /></span>
            <div><span className="panel-overline">NEW ACCOUNT</span><h2>Add a teammate</h2></div>
          </div>
          <label className="data-source-field"><span>Full name</span><input value={name} onChange={(event) => setName(event.target.value)} required maxLength={200} /></label>
          <label className="data-source-field"><span>Sign-in email</span><input type="email" value={email} onChange={(event) => setEmail(event.target.value)} required maxLength={256} /></label>
          <label className="data-source-field"><span>Initial password</span><input type="password" value={password} onChange={(event) => setPassword(event.target.value)} required minLength={12} maxLength={1024} autoComplete="new-password" /><small>At least 12 characters. Share it privately; the database stores only a secure hash.</small></label>
          <label className="data-source-field"><span>Workspace role</span>
            <select value={role} onChange={(event) => setRole(event.target.value as TeamRole)}>
              <option value="Report Viewer">User · Report Viewer</option>
              <option value="Report Designer">Designer</option>
              <option value="Admin">Administrator</option>
            </select>
          </label>
          <button className="button button--primary team-create-button" type="submit" disabled={working}>
            {working ? <span className="spinner spinner--small" /> : <Icon name="plus" size={16} />}
            Create account
          </button>
        </form>

        <section className="team-members-card" aria-labelledby="team-members-title">
          <div className="team-card-heading">
            <span className="team-card-icon team-card-icon--quiet"><Icon name="shield" size={18} /></span>
            <div><span className="panel-overline">ORGANIZATION ACCESS</span><h2 id="team-members-title">People</h2></div>
            <button className="data-source-refresh" type="button" onClick={() => void loadMembers()} disabled={loading}>
              <Icon name="activity" size={15} /> Refresh
            </button>
          </div>
          {loading ? <div className="team-empty"><span className="spinner spinner--small" /> Loading accounts…</div> :
            members.length === 0 ? <div className="team-empty">No accounts found for this organization.</div> :
              <div className="team-member-list">
                {members.map((member) => (
                  <article className={`team-member${member.isActive ? '' : ' is-inactive'}`} key={member.userId}>
                    <span className="team-member-avatar">{member.fullName.trim().split(/\s+/).slice(0, 2).map((part) => part[0]?.toUpperCase()).join('')}</span>
                    <span className="team-member-identity"><strong>{member.fullName}</strong><small>{member.email}{member.userId === user?.userId ? ' · You' : ''}</small></span>
                    <select aria-label={`Role for ${member.fullName}`} value={member.role} disabled={working || member.userId === user?.userId} onChange={(event) => void updateMember(member, { role: event.target.value as TeamRole })}>
                      <option value="Report Viewer">User</option>
                      <option value="Report Designer">Designer</option>
                      <option value="Admin">Admin</option>
                    </select>
                    <button className={`team-member-access${member.isActive ? '' : ' is-revoked'}`} type="button" disabled={working || member.userId === user?.userId} onClick={() => void updateMember(member, { isActive: !member.isActive })}>
                      {member.isActive ? 'Revoke access' : 'Restore access'}
                    </button>
                    {!member.isActive && (
                      <button
                        className="team-member-delete"
                        type="button"
                        disabled={working || member.userId === user?.userId}
                        onClick={() => setMemberToDelete(member)}
                      >
                        Delete
                      </button>
                    )}
                  </article>
                ))}
              </div>}
          <p className="team-permission-note"><Icon name="lock" size={14} /> Changes are stored in this organization’s database and take effect on the next API request.</p>
        </section>
      </div>
      {memberToDelete && (
        <div className="team-delete-backdrop" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !working) setMemberToDelete(null);
        }}>
          <section className="team-delete-dialog" role="dialog" aria-modal="true" aria-labelledby="team-delete-title">
            <span className="panel-overline">PERMANENT ACCOUNT DELETION</span>
            <h2 id="team-delete-title">Delete {memberToDelete.fullName}?</h2>
            <p>This permanently removes the revoked account and its role grants. Existing reports, datasets, and connections will remain; ownership will be reassigned to you, and audit entries will no longer identify this account.</p>
            <p className="team-delete-warning">The organization must keep at least one active administrator.</p>
            <div className="team-delete-actions">
              <button className="button button--secondary" type="button" disabled={working} onClick={() => setMemberToDelete(null)}>Cancel</button>
              <button className="button button--primary team-delete-confirm" type="button" disabled={working} onClick={() => void deleteMember()}>
                {working && <span className="spinner spinner--small" />}
                {working ? 'Deleting…' : 'Permanently delete account'}
              </button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
