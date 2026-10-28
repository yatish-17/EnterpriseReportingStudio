import { useEffect, useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import {
  createOrganizationReportHeader,
  isOrganizationHeaderTemplate,
} from './organizationReportHeader';
import { OrganizationReportHeaderPreview } from './OrganizationReportHeaderPreview';
import {
  reportPrintHeaderElements,
  type ReportPrintHeader,
  type ReportPrintHeaderElement,
} from './reportPrintHeader';
import './OrganizationHeaderTemplatePage.css';

const maximumLogoBytes = 350_000;

async function responseError(response: Response) {
  try {
    const problem: unknown = await response.json();
    if (problem && typeof problem === 'object' && 'title' in problem &&
        typeof problem.title === 'string') {
      return problem.title;
    }
  } catch {
    // Keep the response status as a useful fallback if the API has no problem document.
  }
  return `The request failed (${response.status}).`;
}

function readLogoDataUrl(file: File): Promise<string> {
  if (file.type !== 'image/png' && file.type !== 'image/jpeg') {
    return Promise.reject(new Error('Choose a PNG or JPEG logo image.'));
  }
  if (file.size > maximumLogoBytes) {
    return Promise.reject(new Error('Logo images must be 350 KB or smaller.'));
  }

  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error('The selected logo could not be read.'));
    reader.onload = () => {
      if (typeof reader.result !== 'string') {
        reject(new Error('The selected logo could not be read.'));
        return;
      }
      resolve(reader.result);
    };
    reader.readAsDataURL(file);
  });
}

export function OrganizationHeaderTemplatePage({ onBack }: { onBack: () => void }) {
  const { accessToken, signOut } = useAuth();
  const [title, setTitle] = useState('');
  const [logoDataUrl, setLogoDataUrl] = useState<string | null>(null);
  const [elements, setElements] = useState<ReportPrintHeaderElement[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    async function loadHeader() {
      if (!accessToken) {
        setError('Your session has expired. Sign in again.');
        setLoading(false);
        return;
      }

      try {
        const response = await fetch('/api/reports/organization-header', {
          headers: { Authorization: `Bearer ${accessToken}` },
        });
        if (response.status === 401) {
          await signOut();
          throw new Error('Your session has expired. Sign in again.');
        }
        if (!response.ok) throw new Error(await responseError(response));
        const header = response.status === 204
          ? null
          : await response.json() as ReportPrintHeader | null;
        if (active && header) {
          const legacyTemplate = isOrganizationHeaderTemplate(header.template);
          const loadedTitle = header.title ?? '';
          const loadedLogo = legacyTemplate ? null : header.logoDataUrl ?? null;
          setTitle(loadedTitle);
          setLogoDataUrl(loadedLogo);
          setElements(legacyTemplate
            ? reportPrintHeaderElements({
                title: loadedTitle,
                subtitle: '',
                logoDataUrl: null,
                alignment: 'left',
                elements: [],
              })
            : reportPrintHeaderElements(header));
        }
      } catch (cause) {
        if (active) {
          setError(cause instanceof Error ? cause.message : 'The organization header could not be loaded.');
        }
      } finally {
        if (active) setLoading(false);
      }
    }

    void loadHeader();
    return () => { active = false; };
  }, [accessToken, signOut]);

  async function saveHeader() {
    if (!accessToken) {
      setError('Your session has expired. Sign in again.');
      return;
    }
    if (title.trim().length > 120) {
      setError('Keep the report heading under 120 characters.');
      return;
    }

    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const content = createOrganizationReportHeader(title.trim(), logoDataUrl, elements);
      const header = content.title || content.logoDataUrl ? content : null;
      const response = await fetch('/api/reports/organization-header', {
        method: 'PUT',
        headers: {
          Authorization: `Bearer ${accessToken}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ header }),
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await responseError(response));
      setNotice(header
        ? 'Saved. This header will be used by default on new and existing reports, including published reports.'
        : 'The default report header has been cleared.');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The organization header could not be saved.');
    } finally {
      setSaving(false);
    }
  }

  async function chooseLogo(file?: File) {
    if (!file) return;
    setError(null);
    setNotice(null);
    try {
      setLogoDataUrl(await readLogoDataUrl(file));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The selected logo could not be read.');
    }
  }

  function updateHeaderElement(element: ReportPrintHeaderElement) {
    setElements((current) => [
      ...current.filter((item) => item.type !== element.type),
      element,
    ]);
  }

  const headerPreview = createOrganizationReportHeader(title, logoDataUrl, elements);

  return (
    <section className="organization-header-page" aria-labelledby="organization-header-page-title">
      <header className="organization-header-page-heading">
        <button className="report-back" type="button" onClick={onBack}>
          <Icon name="chevron" size={16} /> Workspace
        </button>
        <span className="login-kicker">ORGANIZATION-WIDE BRANDING</span>
        <h1 id="organization-header-page-title">Default report header</h1>
        <p>Optionally add a heading, a logo, or both. The same header is used when reports are previewed, downloaded, or opened from a published link.</p>
      </header>

      {error && <div className="data-source-alert" role="alert"><Icon name="x" size={17} /><span>{error}</span></div>}

      <div className="organization-header-editor">
        <section className="organization-header-settings" aria-label="Header text settings">
          <span className="organization-header-step">01 · HEADER CONTENT &amp; LAYOUT</span>
          <label className="data-source-field" htmlFor="organization-header-title">
            <span>Heading <small>(optional)</small></span>
            <input
              id="organization-header-title"
              value={title}
              maxLength={120}
              onChange={(event) => { setTitle(event.target.value); setNotice(null); }}
              placeholder="Add a heading"
              disabled={loading || saving}
            />
          </label>
          <div className="organization-header-logo-field">
            <span className="organization-header-control-label">Logo <small>(optional, one image)</small></span>
            <label className={`organization-header-logo-picker${logoDataUrl ? ' has-logo' : ''}`}>
              {logoDataUrl
                ? <img src={logoDataUrl} alt="Selected organization logo" />
                : <><Icon name="file" size={17} /><span>Choose a PNG or JPEG logo</span></>}
              <input
                type="file"
                accept="image/png,image/jpeg"
                aria-label="Choose organization logo"
                disabled={loading || saving}
                onChange={(event) => {
                  void chooseLogo(event.currentTarget.files?.[0]);
                  event.currentTarget.value = '';
                }}
              />
            </label>
            {logoDataUrl && <button
              className="organization-header-remove-logo"
              type="button"
              onClick={() => { setLogoDataUrl(null); setNotice(null); }}
              disabled={loading || saving}
            >Remove logo</button>}
            <p className="organization-header-guidance">PNG or JPEG, up to 350 KB. The logo is sized to fit the report header.</p>
          </div>
          <p className="organization-header-guidance">Leave both fields empty to remove the default header. Drag items in the preview to move them; drag the corner handle to resize. Items stay inside the header.</p>
          {notice && <p className="organization-header-notice" role="status">{notice}</p>}
          <div className="organization-header-actions">
            <button className="button button--secondary" type="button" onClick={onBack} disabled={saving}>Cancel</button>
            <button className="button button--primary" type="button" onClick={() => void saveHeader()} disabled={loading || saving}>
              {saving ? <><span className="spinner spinner--small" /> Saving header</> : <>Save default header <Icon name="arrow" size={15} /></>}
            </button>
          </div>
        </section>

        <section className="organization-header-preview" aria-label="A4 page preview">
          <div className="organization-header-preview-heading">
            <div><span className="organization-header-step">02 · PAGE PREVIEW</span><strong>A4 report sheet</strong></div>
            <span>Header scales with page width</span>
          </div>
          <div className="organization-header-paper">
            <OrganizationReportHeaderPreview
              className="organization-header-editor-preview"
              header={headerPreview}
              interactive
              onElementChange={updateHeaderElement}
            />
            <div className="organization-header-paper-content">
              <div className="organization-header-paper-title" />
              <div className="organization-header-paper-line organization-header-paper-line--short" />
              <div className="organization-header-paper-table">
                <i /><i /><i /><i /><i /><i /><i /><i />
              </div>
              <div className="organization-header-paper-line" />
              <div className="organization-header-paper-line organization-header-paper-line--medium" />
            </div>
          </div>
        </section>
      </div>
    </section>
  );
}
