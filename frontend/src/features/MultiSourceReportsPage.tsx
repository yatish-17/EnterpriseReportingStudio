import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import { ReportFolderPicker, type ReportFolder } from '../components/ReportFolderPicker';
import { SortableReportTable } from '../components/SortableReportTable';
import { resolveOrganizationReportHeader } from './organizationReportHeader';
import { OrganizationReportHeaderPreview } from './OrganizationReportHeaderPreview';
import {
  normalizePrintHeader,
  type ReportPrintHeader,
} from './reportPrintHeader';
import './MultiSourceReportsPage.css';

const ReportChartPreview = lazy(() => import('./ReportChartPreview'));
const reportGroups = ['TVS', 'ECS', 'CHILLER'] as const;
type ReportGroup = string;

function reportGroup(value: string | null | undefined): ReportGroup {
  const normalized = value?.trim();
  if (!normalized) return 'Uncategorized';
  return reportGroups.find((group) => group === normalized.toUpperCase()) ?? normalized;
}

type ParameterType = 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Date' | 'DateTime';
type ComponentType = 'Table' | 'Bar' | 'Line' | 'Pie';
type SortDirection = 'Ascending' | 'Descending';

interface Dataset {
  datasetId: number;
  dataSourceId: number;
  name: string;
  description: string | null;
}

interface DataSource {
  dataSourceId: number;
  name: string;
  databaseName: string;
}

interface Column {
  name: string;
  dataType: string;
}

interface DatasetUse {
  alias: string;
  datasetId: number;
}

interface Relationship {
  leftAlias: string;
  leftColumn: string;
  rightAlias: string;
  rightColumn: string;
  joinType: 'Inner' | 'Left';
}

interface Field {
  alias: string;
  columnName: string;
  displayName: string;
}

interface Component {
  id: string;
  type: ComponentType;
  title: string;
  fields: Field[];
  categoryField?: Field | null;
  valueField?: Field | null;
  order: number;
  sortField?: Field | null;
  sortDirection?: SortDirection | null;
}

interface ReportParameter {
  name: string;
  displayName: string;
  dataType: ParameterType;
  defaultValue: string | null;
  isRequired: boolean;
  allowMultipleValues: boolean;
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
  canEdit: boolean;
  canPublish: boolean;
  reportType: 'MultiSource';
}

interface ReportVersion {
  versionId: number;
  versionNumber: number;
  status: string;
  isCurrentPublished: boolean;
}

interface Design {
  type: 'MultiSource';
  title: string;
  printHeader?: ReportPrintHeader | null;
  datasets: DatasetUse[];
  relationships: Relationship[];
  components: Component[];
  parameters: ReportParameter[];
}

interface Detail {
  report: ReportSummary;
  design: Design;
  versions: ReportVersion[];
}

interface Execution {
  components: {
    component: Component;
    columns: string[];
    rows: unknown[][];
  }[];
  truncated: boolean;
  durationMilliseconds: number;
}

interface DatasetDetail {
  dataset: Dataset;
  queryDefinition: string;
}

interface DatasetPreview {
  columns: Column[];
}

function errorMessage(value: unknown) {
  if (value && typeof value === 'object' && 'title' in value && typeof value.title === 'string') {
    return value.title;
  }
  return 'The request could not be completed.';
}

function parameterValue(parameter: ReportParameter, suppliedValue = parameter.defaultValue ?? '') {
  const value = suppliedValue;
  if (!value) return null;
  if (parameter.dataType === 'Integer' || parameter.dataType === 'Decimal') return Number(value);
  if (parameter.dataType === 'Boolean') return value === 'true';
  return value;
}

function queryParameterNames(query: string) {
  const clean = query
    .replace(/'(?:''|[^'])*'|--[^\r\n]*|\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\[[^\]]*\]|"(?:""|[^"])*"/g, ' ');
  return [...new Set([...clean.matchAll(/(^|[^@])@([A-Za-z_][A-Za-z0-9_]*)/g)].map((match) => match[2]))];
}

function bindDatasetParameters(query: string, parameters: ReportParameter[]) {
  const definitions = new Map(parameters.map((parameter) => [parameter.name.toLowerCase(), parameter]));
  return queryParameterNames(query).map((parameterName) => {
    const definition = definitions.get(parameterName.toLowerCase());
    return {
      name: parameterName,
      dataType: definition?.dataType ?? 'String',
      value: definition ? parameterValue(definition) : null,
    };
  });
}

export function MultiSourceReportsPage({
  initialReportId = null,
  initialDatasetId = null,
  onBack,
}: {
  initialReportId?: number | null;
  initialDatasetId?: number | null;
  onBack: () => void;
}) {
  const { accessToken, signOut } = useAuth();
  const [datasets, setDatasets] = useState<Dataset[]>([]);
  const [reportFolders, setReportFolders] = useState<ReportFolder[]>([]);
  const [sources, setSources] = useState<DataSource[]>([]);
  const [selectedDatasets, setSelectedDatasets] = useState<DatasetUse[]>([]);
  const [columnsByAlias, setColumnsByAlias] = useState<Record<string, Column[]>>({});
  const [relationships, setRelationships] = useState<Relationship[]>([]);
  const [components, setComponents] = useState<Component[]>([]);
  const [parameters, setParameters] = useState<ReportParameter[]>([]);
  const [parameterValues, setParameterValues] = useState<Record<string, string>>({});
  const [componentDialog, setComponentDialog] = useState(false);
  const [parameterDialog, setParameterDialog] = useState(false);
  const [addMenuOpen, setAddMenuOpen] = useState(false);
  const [componentFormOpen, setComponentFormOpen] = useState(false);
  const [parameterFormOpen, setParameterFormOpen] = useState(false);
  const [editingComponentId, setEditingComponentId] = useState<string | null>(null);
  const [editingParameterName, setEditingParameterName] = useState<string | null>(null);
  const [componentType, setComponentType] = useState<ComponentType>('Table');
  const [componentTitle, setComponentTitle] = useState('');
  const [componentFields, setComponentFields] = useState<string[]>([]);
  const [chartCategory, setChartCategory] = useState('');
  const [chartValue, setChartValue] = useState('');
  const [componentSortField, setComponentSortField] = useState('');
  const [componentSortDirection, setComponentSortDirection] = useState<SortDirection>('Ascending');
  const [parameterName, setParameterName] = useState('');
  const [parameterDisplayName, setParameterDisplayName] = useState('');
  const [parameterType, setParameterType] = useState<ParameterType>('String');
  const [parameterDefault, setParameterDefault] = useState('');
  const [parameterRequired, setParameterRequired] = useState(false);
  const [name, setName] = useState('');
  const [title, setTitle] = useState('');
  const [defaultPrintHeader, setDefaultPrintHeader] = useState<ReportPrintHeader | null>(null);
  const [description, setDescription] = useState('');
  const [category, setCategory] = useState('');
  const [detail, setDetail] = useState<Detail | null>(null);
  const [execution, setExecution] = useState<Execution | null>(null);
  const [designOpen, setDesignOpen] = useState(Boolean(initialReportId));
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [shareUrl, setShareUrl] = useState<string | null>(null);
  const [shareCopied, setShareCopied] = useState(false);
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false);
  const [detailsDialogOpen, setDetailsDialogOpen] = useState(false);
  const [publishDialogOpen, setPublishDialogOpen] = useState(false);
  const [publishFoldersLoading, setPublishFoldersLoading] = useState(false);
  const [publishCategory, setPublishCategory] = useState<ReportGroup>('');

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
    if (!response.ok) {
      let problem: unknown;
      try { problem = await response.json(); } catch { problem = null; }
      throw new Error(errorMessage(problem));
    }
    if (response.status === 204) return undefined as T;
    return response.json() as Promise<T>;
  }, [accessToken, signOut]);

  const selectedColumns = useMemo(() => selectedDatasets.flatMap((dataset) =>
    (columnsByAlias[dataset.alias] ?? []).map((column) => ({
      alias: dataset.alias,
      columnName: column.name,
      label: `${dataset.alias}.${column.name}`,
      displayName: `${sources.find((source) => source.dataSourceId ===
        datasets.find((item) => item.datasetId === dataset.datasetId)?.dataSourceId)?.name ?? dataset.alias} · ${column.name}`,
      dataType: column.dataType,
    }))), [columnsByAlias, datasets, selectedDatasets, sources]);

  useEffect(() => {
    let active = true;
    async function load() {
      setLoading(true);
      setError(null);
      try {
        const [datasetRows, sourceRows, organizationHeader] = await Promise.all([
          request<Dataset[]>('/api/datasets'),
          request<DataSource[]>('/api/data-sources'),
          request<ReportPrintHeader | null>('/api/reports/organization-header'),
        ]);
        if (!active) return;
        setDatasets(datasetRows);
        setSources(sourceRows);
        setDefaultPrintHeader(normalizePrintHeader(
          await resolveOrganizationReportHeader(organizationHeader),
        ));
        if (initialDatasetId !== null) {
          const dataset = datasetRows.find((item) => item.datasetId === initialDatasetId);
          if (!dataset) {
            throw new Error('That dataset is no longer available. Return to the dataset library and try again.');
          }
          const datasetDetail = await request<DatasetDetail>(`/api/datasets/${initialDatasetId}`);
          const preview = await request<DatasetPreview>(`/api/datasets/${initialDatasetId}/preview`, {
            method: 'POST',
            body: JSON.stringify({ parameters: bindDatasetParameters(datasetDetail.queryDefinition, []) }),
          });
          if (!active) return;
          setName(dataset.name);
          setTitle(dataset.name);
          setSelectedDatasets([{ alias: 'source1', datasetId: initialDatasetId }]);
          setColumnsByAlias({ source1: preview.columns });
          setDesignOpen(true);
        }
        if (initialReportId) {
          const opened = await request<Detail>(`/api/multi-reports/${initialReportId}`);
          if (!active) return;
          setDetail(opened);
          setName(opened.report.name);
          setDescription(opened.report.description ?? '');
          setCategory(opened.report.category ? reportGroup(opened.report.category) : '');
          setTitle(opened.design.title);
          setSelectedDatasets(opened.design.datasets);
          setRelationships(opened.design.relationships);
          setComponents(opened.design.components);
          setParameters(opened.design.parameters);
          setParameterValues(Object.fromEntries(opened.design.parameters.map((parameter) => [
            parameter.name, parameter.defaultValue ?? '',
          ])));
          if (opened.versions.some((version) => version.isCurrentPublished)) {
            try {
              const share = await request<{ token: string }>(`/api/reports/${opened.report.reportId}/share-token`);
              if (!active) return;
              setShareUrl(`${window.location.origin}/shared/${encodeURIComponent(share.token)}`);
            } catch {
              if (active) setError('The published report link could not be loaded.');
            }
          }
          for (const selected of opened.design.datasets) {
            const datasetDetail = await request<DatasetDetail>(`/api/datasets/${selected.datasetId}`);
            const preview = await request<DatasetPreview>(`/api/datasets/${selected.datasetId}/preview`, {
              method: 'POST',
              body: JSON.stringify({ parameters: bindDatasetParameters(datasetDetail.queryDefinition, opened.design.parameters) }),
            });
            if (!active) return;
            setColumnsByAlias((current) => ({ ...current, [selected.alias]: preview.columns }));
          }
        }
      } catch (cause) {
        if (active) setError(cause instanceof Error ? cause.message : 'Workspace data could not be loaded.');
      } finally {
        if (active) setLoading(false);
      }
    }
    void load();
    return () => { active = false; };
  }, [initialDatasetId, initialReportId, request]);

  async function addDataset(datasetId: number) {
    if (!datasetId || selectedDatasets.some((item) => item.datasetId === datasetId)) return;
    let aliasNumber = 1;
    while (selectedDatasets.some((item) => item.alias === `source${aliasNumber}`)) aliasNumber++;
    const alias = `source${aliasNumber}`;
    setWorking(true);
    setError(null);
    try {
      const datasetDetail = await request<DatasetDetail>(`/api/datasets/${datasetId}`);
      const preview = await request<DatasetPreview>(`/api/datasets/${datasetId}/preview`, {
        method: 'POST',
        body: JSON.stringify({ parameters: bindDatasetParameters(datasetDetail.queryDefinition, parameters) }),
      });
      setSelectedDatasets((current) => [...current, { alias, datasetId }]);
      setColumnsByAlias((current) => ({ ...current, [alias]: preview.columns }));
      if (selectedDatasets.length > 0) {
        const left = selectedDatasets[0];
        setRelationships((current) => [...current, {
          leftAlias: left.alias,
          leftColumn: columnsByAlias[left.alias]?.[0]?.name ?? '',
          rightAlias: alias,
          rightColumn: preview.columns[0]?.name ?? '',
          joinType: 'Inner',
        }]);
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The dataset fields could not be loaded.');
    } finally {
      setWorking(false);
    }
  }

  function removeDataset(alias: string) {
    const remaining = selectedDatasets.filter((item) => item.alias !== alias);
    setSelectedDatasets(remaining);
    const existing = relationships.filter((join) => join.rightAlias !== alias);
    setRelationships(remaining.slice(1).map((dataset) => {
      const prior = existing.find((join) => join.rightAlias === dataset.alias);
      const leftAlias = remaining[0].alias;
      const leftColumns = columnsByAlias[leftAlias] ?? [];
      const rightColumns = columnsByAlias[dataset.alias] ?? [];
      return {
        leftAlias,
        leftColumn: leftColumns.some((column) => column.name === prior?.leftColumn)
          ? prior!.leftColumn : leftColumns[0]?.name ?? '',
        rightAlias: dataset.alias,
        rightColumn: rightColumns.some((column) => column.name === prior?.rightColumn)
          ? prior!.rightColumn : rightColumns[0]?.name ?? '',
        joinType: prior?.joinType ?? 'Inner',
      };
    }));
    setComponents((current) => current.filter((component) =>
      component.fields.every((field) => field.alias !== alias)));
    setColumnsByAlias((current) => Object.fromEntries(
      Object.entries(current).filter(([key]) => key !== alias)));
  }

  function fieldFromKey(key: string): Field | null {
    const [alias, ...columnParts] = key.split('.');
    const columnName = columnParts.join('.');
    if (!alias || !columnName) return null;
    const selectedColumn = selectedColumns.find((field) =>
      field.alias === alias && field.columnName === columnName);
    return { alias, columnName, displayName: selectedColumn?.displayName ?? `${alias} · ${columnName}` };
  }

  function addComponent() {
    const fields = componentFields.map(fieldFromKey).filter((field): field is Field => field !== null);
    const categoryField = fieldFromKey(chartCategory);
    const valueField = fieldFromKey(chartValue);
    const sortField = componentType === 'Table' && componentSortField && componentFields.includes(componentSortField)
      ? fieldFromKey(componentSortField)
      : null;
    if (!componentTitle.trim() || (componentType === 'Table' ? fields.length === 0 : !categoryField || !valueField)) {
      setError('Add a title and select the required fields for this report component.');
      return;
    }
    const component: Component = {
      id: editingComponentId ?? crypto.randomUUID(),
      type: componentType,
      title: componentTitle.trim(),
      fields: componentType === 'Table' ? fields : [categoryField!, valueField!],
      categoryField: componentType === 'Table' ? null : categoryField,
      valueField: componentType === 'Table' ? null : valueField,
      order: components.length,
      sortField,
      sortDirection: sortField ? componentSortDirection : null,
    };
    setComponents((current) => editingComponentId
      ? current.map((item) => item.id === editingComponentId ? component : item)
      : [...current, component]);
    setExecution(null);
    setComponentFormOpen(false);
    setEditingComponentId(null);
    setComponentTitle('');
    setComponentFields([]);
    setChartCategory('');
    setChartValue('');
    setComponentSortField('');
    setComponentSortDirection('Ascending');
    setError(null);
  }

  function addParameter() {
    const cleanName = parameterName.trim().replace(/^@/, '');
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(cleanName) ||
      parameters.some((parameter) => parameter.name.toLowerCase() === cleanName.toLowerCase() &&
        parameter.name !== editingParameterName)) {
      setError('Enter a unique parameter name using letters, numbers, and underscores.');
      return;
    }
    const next: ReportParameter = {
      name: cleanName,
      displayName: parameterDisplayName.trim() || cleanName,
      dataType: parameterType,
      defaultValue: parameterDefault.trim() || null,
      isRequired: parameterRequired,
      allowMultipleValues: false,
    };
    setParameters((current) => editingParameterName
      ? current.map((parameter) => parameter.name === editingParameterName ? next : parameter)
      : [...current, next]);
    setExecution(null);
    setParameterValues((current) => {
      const updated = { ...current };
      const priorValue = current[editingParameterName ?? cleanName] ?? next.defaultValue ?? '';
      if (editingParameterName && editingParameterName !== cleanName) delete updated[editingParameterName];
      updated[cleanName] = priorValue;
      return updated;
    });
    setParameterFormOpen(false);
    setEditingParameterName(null);
    setParameterName('');
    setParameterDisplayName('');
    setParameterDefault('');
    setError(null);
  }

  function editComponent(component: Component) {
    setEditingComponentId(component.id);
    setComponentType(component.type);
    setComponentTitle(component.title);
    setComponentFields(component.type === 'Table'
      ? component.fields.map((field) => `${field.alias}.${field.columnName}`)
      : []);
    setChartCategory(component.categoryField ? `${component.categoryField.alias}.${component.categoryField.columnName}` : '');
    setChartValue(component.valueField ? `${component.valueField.alias}.${component.valueField.columnName}` : '');
    setComponentSortField(component.sortField ? `${component.sortField.alias}.${component.sortField.columnName}` : '');
    setComponentSortDirection(component.sortDirection ?? 'Ascending');
    setComponentFormOpen(true);
  }

  function editParameter(parameter: ReportParameter) {
    setEditingParameterName(parameter.name);
    setParameterName(parameter.name);
    setParameterDisplayName(parameter.displayName);
    setParameterType(parameter.dataType);
    setParameterDefault(parameter.defaultValue ?? '');
    setParameterRequired(parameter.isRequired);
    setParameterFormOpen(true);
  }

  function newComponent(type: ComponentType = 'Table') {
    setComponentDialog(true);
    setEditingComponentId(null);
    setComponentType(type);
    setComponentTitle('');
    setComponentFields([]);
    setChartCategory('');
    setChartValue('');
    setComponentSortField('');
    setComponentSortDirection('Ascending');
    setComponentFormOpen(true);
  }

  function newParameter() {
    setEditingParameterName(null);
    setParameterName('');
    setParameterDisplayName('');
    setParameterType('String');
    setParameterDefault('');
    setParameterRequired(false);
    setParameterFormOpen(true);
  }

  function buildInput() {
    if (selectedDatasets.length < 1 || !name.trim() || !title.trim() || components.length === 0) {
      throw new Error('Add a report name, title, at least one dataset, and one report component.');
    }
    return {
      name: name.trim(),
      description: description.trim() || null,
      category: category.trim() || null,
      design: {
        type: 'MultiSource' as const,
        title: title.trim(),
        printHeader: null,
        datasets: selectedDatasets,
        relationships,
        components: components.map((component, order) => ({ ...component, order })),
        parameters,
      },
    };
  }

  async function saveDraft(): Promise<Detail | null> {
    setWorking(true);
    setError(null);
    setNotice(null);
    try {
      const input = buildInput();
      const saved = detail
        ? await request<Detail>(`/api/multi-reports/${detail.report.reportId}`, {
          method: 'PUT', body: JSON.stringify(input),
        })
        : await request<Detail>('/api/multi-reports', {
          method: 'POST', body: JSON.stringify(input),
        });
      setDetail(saved);
      setNotice(`Draft saved as version ${saved.report.versionNumber}.`);
      return saved;
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The draft could not be saved.');
      return null;
    } finally {
      setWorking(false);
    }
  }

  async function runPreview() {
    const saved = await saveDraft();
    if (!saved) return;
    const missing = parameters.find((parameter) =>
      parameter.isRequired && !(parameterValues[parameter.name] ?? parameter.defaultValue ?? '').trim());
    if (missing) {
      setError(`Enter a value for "${missing.displayName}" before previewing.`);
      return;
    }
    setWorking(true);
    setError(null);
    try {
      const result = await request<Execution>(`/api/multi-reports/${saved.report.reportId}/execute`, {
        method: 'POST',
        body: JSON.stringify({
          versionId: saved.report.versionId,
          parameters: Object.fromEntries(parameters.map((parameter) => [
            parameter.name, parameterValue(parameter, parameterValues[parameter.name] ?? ''),
          ])),
        }),
      });
      setExecution(result);
      if (result.components.length > 0 && result.components.every((component) => component.rows.length === 0)) {
        const joinSummary = relationships.map((relationship) =>
          `${relationship.joinType.toUpperCase()} ${relationship.leftAlias}.${relationship.leftColumn} = ${relationship.rightAlias}.${relationship.rightColumn}`)
          .join('; ');
        setNotice(`Preview returned 0 joined rows. Check that both sides of the join contain matching values${joinSummary ? ` (${joinSummary})` : ''}, and verify report parameters. Use a left join only if unmatched rows from the first dataset should remain.`);
      } else {
        setNotice('Preview completed with data.');
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report preview could not be loaded.');
    } finally {
      setWorking(false);
    }
  }

  async function publish() {
    if (!detail) return;
    setWorking(true);
    setError(null);
    try {
      const published = await request<ReportSummary>(
        `/api/multi-reports/${detail.report.reportId}/versions/${detail.report.versionId}/publish`,
        { method: 'POST', body: JSON.stringify({ category: publishCategory }) },
      );
      setPublishDialogOpen(false);
      setDetail((current) => current ? {
        ...current,
        report: published,
        versions: current.versions.map((version) => ({
          ...version,
          status: version.versionId === published.versionId ? 'Published' : version.status,
          isCurrentPublished: version.versionId === published.versionId,
        })),
      } : current);
      setNotice(`Version ${published.versionNumber} is now published.`);
      try {
        const share = await request<{ token: string }>(`/api/reports/${published.reportId}/share-token`);
        setShareUrl(`${window.location.origin}/shared/${encodeURIComponent(share.token)}`);
        setShareCopied(false);
      } catch (cause) {
        setError(cause instanceof Error
          ? `Published successfully, but the share URL could not be loaded: ${cause.message}`
          : 'Published successfully, but the share URL could not be loaded.');
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be published.');
    } finally {
      setWorking(false);
    }
  }

  async function deleteReport() {
    if (!detail || detail.report.versionStatus !== 'Draft') return;
    setWorking(true);
    setError(null);
    try {
      await request<void>(`/api/multi-reports/${detail.report.reportId}`, { method: 'DELETE' });
      setDeleteDialogOpen(false);
      onBack();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be deleted.');
    } finally {
      setWorking(false);
    }
  }

  async function openPublishDialog() {
    if (!detail) return;
    setPublishDialogOpen(true);
    setError(null);
    setPublishFoldersLoading(true);
    try {
      const folders = await request<ReportFolder[]>('/api/reports/folders');
      setReportFolders(folders);
      const preferred = folders.find((folder) =>
        folder.name.localeCompare(detail.report.category ?? '', undefined, { sensitivity: 'accent' }) === 0);
      setPublishCategory(preferred?.name ?? folders[0]?.name ?? '');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Dashboard folders could not be loaded.');
    } finally {
      setPublishFoldersLoading(false);
    }
  }

  async function createReportFolder(folderName: string) {
    const folder = await request<ReportFolder>('/api/reports/folders', {
      method: 'POST',
      body: JSON.stringify({ name: folderName }),
    });
    setReportFolders((current) => [...current, folder].sort((left, right) =>
      left.name.localeCompare(right.name)));
  }

  async function deleteReportFolder(folder: ReportFolder) {
    await request<void>(`/api/reports/folders/${encodeURIComponent(folder.name)}`, {
      method: 'DELETE',
    });
    setReportFolders((current) => current.filter((item) => item.name !== folder.name));
    setPublishCategory((current) => current === folder.name
      ? reportFolders.find((item) => item.name !== folder.name)?.name ?? ''
      : current);
  }

  async function copyShareUrl() {
    if (!shareUrl) return;
    try {
      await navigator.clipboard.writeText(shareUrl);
      setShareCopied(true);
    } catch {
      setError('The link is ready to copy. Select the URL and copy it manually.');
    }
  }

  if (loading) {
    return <section className="multi-report-page"><div className="data-source-empty"><span className="spinner spinner--large" /><p>Preparing the unified report designer…</p></div></section>;
  }

  const canStartDesign = Boolean(name.trim() && title.trim()) &&
    selectedDatasets.length >= 1 &&
    relationships.length === selectedDatasets.length - 1 &&
    relationships.every((relationship) => relationship.leftColumn && relationship.rightColumn);

  return (
    <section className="multi-report-page" aria-labelledby="multi-report-title">
      <header className="multi-report-header">
        <div>
          <button className="report-back" type="button" onClick={onBack}><Icon name="chevron" size={15} /> Report library</button>
          <span className="login-kicker">UNIFIED REPORT DESIGNER</span>
          <h1 id="multi-report-title">{designOpen ? (detail ? 'Edit report' : 'Design a report') : (detail ? 'Set up report' : 'Create a report')}</h1>
          <p>{designOpen
            ? 'Arrange your report on the page. Manage tables, charts, and parameters from the toolbar.'
            : 'Choose one or more saved datasets, then design the report on one canvas.'}</p>
        </div>
      </header>
      {error && <div className="data-source-alert" role="alert"><Icon name="x" size={16} />{error}</div>}
      {notice && <div className="multi-report-notice" role="status"><Icon name="check" size={16} />{notice}</div>}
      {shareUrl && <div className="multi-report-share">
        <span><strong>Published report link</strong><small>This stable link always opens the current published version.</small></span>
        <input value={shareUrl} readOnly aria-label="Published report URL" onFocus={(event) => event.currentTarget.select()} />
        <button className="button button--secondary" type="button" onClick={() => void copyShareUrl()}>{shareCopied ? 'Copied' : 'Copy link'}</button>
      </div>}

      {!designOpen ? (
        <div className="multi-report-layout multi-report-setup-layout">
          <aside className="multi-report-rail">
            <span className="multi-report-section-label">REPORT DETAILS</span>
            <label>Report name<input value={name} onChange={(event) => setName(event.target.value)} placeholder="e.g. Regional performance" /></label>
            <label>Page title<input value={title} onChange={(event) => setTitle(event.target.value)} placeholder="Report heading" /></label>
            <label>Dashboard folder preference<select value={category} onChange={(event) => setCategory(event.target.value)}><option value="">Choose when publishing</option>{reportFolders.map((folder) => <option key={folder.name} value={folder.name}>{folder.name}</option>)}</select></label>
            <label>Description<textarea value={description} onChange={(event) => setDescription(event.target.value)} rows={3} placeholder="What this report covers" /></label>
          </aside>

          <div className="multi-report-main">
          <section className="multi-report-panel">
            <div className="multi-report-panel-heading">
              <div><span className="multi-report-section-label">DATA SOURCES</span><h2>Choose saved datasets</h2><p>Only datasets already shared with your account are available here.</p></div>
              <select value="" onChange={(event) => void addDataset(Number(event.target.value))} disabled={working}>
                <option value="">Add dataset…</option>
                {datasets.filter((dataset) => !selectedDatasets.some((item) => item.datasetId === dataset.datasetId)).map((dataset) => {
                  const source = sources.find((item) => item.dataSourceId === dataset.dataSourceId);
                  return <option key={dataset.datasetId} value={dataset.datasetId}>{source?.databaseName ?? source?.name ?? 'Source'} · {dataset.name}</option>;
                })}
              </select>
            </div>
            {selectedDatasets.length === 0 ? <div className="multi-report-empty"><Icon name="database" size={20} /><span>Select one or more saved datasets. Add another dataset only when you want to join data.</span></div> :
              <div className="multi-report-dataset-list">
                {selectedDatasets.map((selected, index) => {
                  const dataset = datasets.find((item) => item.datasetId === selected.datasetId);
                  const source = sources.find((item) => item.dataSourceId === dataset?.dataSourceId);
                  return <article className="multi-report-dataset" key={selected.alias}>
                    <div className="multi-report-dataset-heading">
                      <span className="multi-report-dataset-index">{String(index + 1).padStart(2, '0')}</span>
                      <div><strong>{selected.alias}</strong><small>{source?.databaseName ?? source?.name ?? 'Database'} · {dataset?.name ?? 'Dataset'}</small></div>
                      <button type="button" aria-label={`Remove ${selected.alias}`} onClick={() => removeDataset(selected.alias)}><Icon name="x" size={15} /></button>
                    </div>
                    <div className="multi-report-columns">
                      {(columnsByAlias[selected.alias] ?? []).map((column) => <span key={column.name}>{column.name}<small>{column.dataType}</small></span>)}
                    </div>
                    {index > 0 && <div className="multi-report-join">
                      <span>JOIN</span>
                      <select value={relationships.find((join) => join.rightAlias === selected.alias)?.leftColumn ?? ''} onChange={(event) => setRelationships((current) => current.map((join) => join.rightAlias === selected.alias ? { ...join, leftColumn: event.target.value } : join))}>
                        {(columnsByAlias[selectedDatasets[0].alias] ?? []).map((column) => <option key={column.name} value={column.name}>{selectedDatasets[0].alias}.{column.name}</option>)}
                      </select>
                      <select value={relationships.find((join) => join.rightAlias === selected.alias)?.joinType ?? 'Inner'} onChange={(event) => setRelationships((current) => current.map((join) => join.rightAlias === selected.alias ? { ...join, joinType: event.target.value as 'Inner' | 'Left' } : join))}>
                        <option value="Inner">Inner join</option><option value="Left">Left join</option>
                      </select>
                      <select value={relationships.find((join) => join.rightAlias === selected.alias)?.rightColumn ?? ''} onChange={(event) => setRelationships((current) => current.map((join) => join.rightAlias === selected.alias ? { ...join, rightColumn: event.target.value } : join))}>
                        {(columnsByAlias[selected.alias] ?? []).map((column) => <option key={column.name} value={column.name}>{selected.alias}.{column.name}</option>)}
                      </select>
                    </div>}
                  </article>;
                })}
              </div>}
          </section>
            <div className="multi-report-setup-footer">
              <span>{canStartDesign ? 'Your report details and dataset selection are ready.' : 'Add a name and title, choose at least one dataset, and complete any joins to continue.'}</span>
              <button className="button button--primary" type="button" onClick={() => { setError(null); setDesignOpen(true); }} disabled={!canStartDesign || working}>
                <Icon name="activity" size={16} /> Open designer
              </button>
            </div>
          </div>
        </div>
      ) : (
        <>
          <div className="multi-report-design-toolbar">
            <div className="multi-report-design-context">
              <button className="button button--secondary" type="button" onClick={() => { setDesignOpen(false); setExecution(null); }}>
                <Icon name="chevron" size={15} /> Report setup
              </button>
              <span><strong>{name || title || 'Untitled report'}</strong><small>{selectedDatasets.length} {selectedDatasets.length === 1 ? 'dataset' : 'datasets'} · {components.length} report parts</small></span>
            </div>
            <div className="multi-report-design-tools">
              <button className="button button--secondary" type="button" onClick={() => setDetailsDialogOpen(true)}>
                Edit report details
              </button>
              <button className="button button--secondary" type="button" onClick={() => { setParameterFormOpen(false); setParameterDialog(true); }}>
                Parameters <span>{parameters.length}</span>
              </button>
              <button className="button button--secondary" type="button" onClick={() => { setComponentFormOpen(false); setComponentDialog(true); }}>
                Report parts <span>{components.length}</span>
              </button>
              <div className="multi-report-add-menu">
                <button
                  className="button button--secondary"
                  type="button"
                  aria-haspopup="menu"
                  aria-expanded={addMenuOpen}
                  onClick={() => setAddMenuOpen((open) => !open)}
                  disabled={selectedColumns.length === 0}
                >
                  <Icon name="plus" size={15} /> Add
                </button>
                {addMenuOpen && <div className="multi-report-add-menu-options" role="menu" aria-label="Add report part">
                  {(['Table', 'Bar', 'Line', 'Pie'] as const).map((type) => (
                    <button
                      key={type}
                      type="button"
                      role="menuitem"
                      disabled={type !== 'Table' && (
                        selectedColumns.length < 2 ||
                        !selectedColumns.some((field) => /int|decimal|numeric|money|float|real/i.test(field.dataType))
                      )}
                      onClick={() => { setAddMenuOpen(false); newComponent(type); }}
                    >
                      {type === 'Table' ? 'Table' : `${type} chart`}
                    </button>
                  ))}
                </div>}
              </div>
              {detail && <span className={`multi-report-version-badge${detail.report.versionStatus === 'Draft' ? ' is-draft' : ''}`}>
                {detail.report.versionStatus} · v{detail.report.versionNumber}
              </span>}
              <button className="button button--secondary" type="button" onClick={() => void runPreview()} disabled={working || components.length === 0}>
                <Icon name="eye" size={15} /> Preview
              </button>
              {detail?.report.versionStatus === 'Draft' && detail.report.canPublish &&
                <button className="button button--secondary" type="button" onClick={() => {
                  void openPublishDialog();
                }} disabled={working}>
                  <Icon name="check" size={15} /> Publish
                </button>}
              {detail?.report.versionStatus === 'Draft' &&
                <button className="button button--secondary multi-report-delete-button" type="button" onClick={() => setDeleteDialogOpen(true)} disabled={working}>
                  <Icon name="x" size={15} /> Delete report
                </button>}
              <button className="button button--primary" type="button" onClick={() => void saveDraft()} disabled={working}>
                {working && <span className="spinner spinner--small" />} Save draft
              </button>
            </div>
          </div>

          <div className="multi-report-canvas-wrap">
            <article className="multi-report-canvas" aria-label="Report design page">
                  {execution && defaultPrintHeader && <OrganizationReportHeaderPreview
                    className="multi-report-preview-header"
                    header={defaultPrintHeader}
                  />}
                  <header className="multi-report-canvas-heading">
                <span className="multi-report-section-label">{category || 'REPORT'}</span>
                <h2>{title || 'Untitled report'}</h2>
                {description && <p>{description}</p>}
              </header>
              {components.length === 0 ? (
                <div className="multi-report-canvas-empty">
                  <Icon name="file" size={28} />
                  <h3>Start designing your report</h3>
                  <p>Use Add in the toolbar to choose a table or chart. Your report components will be arranged on this page.</p>
                </div>
              ) : components.map((component, index) => {
                const result = execution?.components.find((item) => item.component.id === component.id);
                return <section className="multi-report-canvas-part" key={component.id}>
                  <div className="multi-report-canvas-part-heading">
                    <span>{String(index + 1).padStart(2, '0')} · {component.type.toUpperCase()}</span>
                    <h3>{component.title}</h3>
                  </div>
                  {result?.component.type === 'Table' ? <div className="multi-report-table-wrap">
                    <SortableReportTable columns={result.columns} rows={result.rows} maxRows={12} />
                  </div> : result ? <Suspense fallback={<p>Loading chart preview…</p>}><ReportChartPreview
                    type={result.component.type as 'Bar' | 'Line' | 'Pie'}
                    categoryName={result.columns[0] ?? 'Category'}
                    valueName={result.columns[1] ?? 'Value'}
                    data={result.rows.flatMap((row) => {
                      const value = row[1] == null ? Number.NaN : Number(row[1]);
                      return Number.isFinite(value) ? [{ category: String(row[0] ?? '(blank)'), value }] : [];
                    })}
                  /></Suspense> : <p className="multi-report-canvas-placeholder">
                    {component.type === 'Table'
                      ? component.fields.map((field) => `${field.alias}.${field.columnName}`).join(' · ')
                      : `${component.categoryField?.displayName ?? 'Category'} by ${component.valueField?.displayName ?? 'Value'} · Preview to see data`}
                  </p>}
                </section>;
              })}
              {execution && <p className="multi-report-canvas-status">
                {execution.durationMilliseconds} ms · {execution.truncated ? 'Results were limited for preview.' : 'Preview complete.'}
              </p>}
            </article>
          </div>
        </>
      )}

      {componentDialog && <div className="multi-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setComponentDialog(false); }}>
        <section className="multi-report-modal" role="dialog" aria-modal="true" aria-labelledby="component-modal-title">
          <header><div><span className="login-kicker">REPORT PARTS</span><h2 id="component-modal-title">{componentFormOpen ? (editingComponentId ? 'Edit report part' : 'Add a table or chart') : 'Manage report parts'}</h2></div><button className="icon-button" type="button" aria-label="Close" onClick={() => setComponentDialog(false)}><Icon name="x" size={17} /></button></header>
          {!componentFormOpen && <div className="multi-report-manage-list">
            {components.length === 0 ? <p>No report parts yet. Add a table or chart to get started.</p> : components.map((component) => <div key={component.id}><span><strong>{component.title}</strong><small>{component.type} · {component.fields.length} fields{component.sortField ? ` · sorted ${component.sortDirection?.toLowerCase()} by ${component.sortField.columnName}` : ''}</small></span><button type="button" onClick={() => editComponent(component)}>Edit</button><button type="button" aria-label={`Remove ${component.title}`} onClick={() => setComponents((current) => current.filter((item) => item.id !== component.id))}><Icon name="x" size={14} /></button></div>)}
            <button className="button button--secondary" type="button" onClick={() => newComponent()}><Icon name="plus" size={15} /> New report part</button>
          </div>}
          {componentFormOpen && <>
            <label>Part type<select value={componentType} onChange={(event) => setComponentType(event.target.value as ComponentType)}><option>Table</option><option>Bar</option><option>Line</option><option>Pie</option></select></label>
            <label>Title<input value={componentTitle} onChange={(event) => setComponentTitle(event.target.value)} placeholder="e.g. Revenue by region" /></label>
            {componentType === 'Table' ? <>
              <fieldset><legend>Columns</legend><div className="multi-report-field-checks">{selectedColumns.map((field) => <label key={field.label}><input type="checkbox" checked={componentFields.includes(field.label)} onChange={() => setComponentFields((current) => current.includes(field.label) ? current.filter((value) => value !== field.label) : [...current, field.label])} /><span>{field.displayName}<small>{field.dataType}</small></span></label>)}</div></fieldset>
              <label>Sort rows by<select value={componentSortField} onChange={(event) => setComponentSortField(event.target.value)}>
                <option value="">Keep source order</option>
                {selectedColumns.filter((field) => componentFields.includes(field.label)).map((field) =>
                  <option key={field.label} value={field.label}>{field.displayName}</option>)}
              </select></label>
              <label>Sort direction<select value={componentSortDirection} onChange={(event) => setComponentSortDirection(event.target.value as SortDirection)} disabled={!componentSortField}>
                <option value="Ascending">Ascending (A to Z, lowest to highest)</option>
                <option value="Descending">Descending (Z to A, highest to lowest)</option>
              </select></label>
            </> :
              <>
                <label>Category field<select value={chartCategory} onChange={(event) => setChartCategory(event.target.value)}><option value="">Choose a category…</option>{selectedColumns.map((field) => <option key={field.label} value={field.label}>{field.displayName}</option>)}</select></label>
                <label>Numeric value<select value={chartValue} onChange={(event) => setChartValue(event.target.value)}><option value="">Choose a value…</option>{selectedColumns.filter((field) => /int|decimal|numeric|money|float|real/i.test(field.dataType)).map((field) => <option key={field.label} value={field.label}>{field.displayName}</option>)}</select></label>
              </>}
            <footer><button className="button button--secondary" type="button" onClick={() => setComponentFormOpen(false)}>Cancel</button><button className="button button--primary" type="button" onClick={addComponent}>{editingComponentId ? 'Save changes' : 'Add to report'}</button></footer>
          </>}
        </section>
      </div>}

      {parameterDialog && <div className="multi-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setParameterDialog(false); }}>
        <section className="multi-report-modal" role="dialog" aria-modal="true" aria-labelledby="parameter-modal-title">
          <header><div><span className="login-kicker">QUERY INPUTS</span><h2 id="parameter-modal-title">{parameterFormOpen ? (editingParameterName ? 'Edit parameter' : 'Add a report parameter') : 'Manage parameters'}</h2></div><button className="icon-button" type="button" aria-label="Close" onClick={() => setParameterDialog(false)}><Icon name="x" size={17} /></button></header>
          {!parameterFormOpen && <div className="multi-report-manage-list">
            {parameters.length === 0 ? <p>No parameters yet. Add one when a dataset query requires an input.</p> : parameters.map((parameter) => <div key={parameter.name}><span><strong>{parameter.displayName}</strong><small>@{parameter.name} · {parameter.dataType}{parameter.isRequired ? ' · required' : ''}</small></span><button type="button" onClick={() => editParameter(parameter)}>Edit</button><button type="button" aria-label={`Remove ${parameter.displayName}`} onClick={() => setParameters((current) => current.filter((item) => item.name !== parameter.name))}><Icon name="x" size={14} /></button></div>)}
            <button className="button button--secondary" type="button" onClick={newParameter}><Icon name="plus" size={15} /> New parameter</button>
          </div>}
          {parameterFormOpen && <>
            <label>Parameter name<input value={parameterName} onChange={(event) => setParameterName(event.target.value)} placeholder="StartDate" /></label>
            <label>Display name<input value={parameterDisplayName} onChange={(event) => setParameterDisplayName(event.target.value)} placeholder="Start date" /></label>
            <label>Type<select value={parameterType} onChange={(event) => setParameterType(event.target.value as ParameterType)}>
              <option value="String">Text</option><option value="Integer">Integer</option><option value="Decimal">Decimal</option>
              <option value="Boolean">Yes / no</option><option value="Date">Date</option><option value="DateTime">Date and time</option>
            </select></label>
            <label>Default value
              {parameterType === 'Boolean' ? <select value={parameterDefault} onChange={(event) => setParameterDefault(event.target.value)}>
                <option value="">No default</option><option value="true">Yes</option><option value="false">No</option>
              </select> : <input
                type={parameterType === 'Integer' || parameterType === 'Decimal' ? 'number' : parameterType === 'Date' ? 'date' : parameterType === 'DateTime' ? 'datetime-local' : 'text'}
                step={parameterType === 'Decimal' ? 'any' : undefined}
                value={parameterDefault}
                onChange={(event) => setParameterDefault(event.target.value)}
              />}
            </label>
            <label className="multi-report-required-toggle"><input type="checkbox" checked={parameterRequired} onChange={(event) => setParameterRequired(event.target.checked)} />Required to run this report</label>
            <footer><button className="button button--secondary" type="button" onClick={() => setParameterFormOpen(false)}>Cancel</button><button className="button button--primary" type="button" onClick={addParameter}>{editingParameterName ? 'Save changes' : 'Add parameter'}</button></footer>
          </>}
          {!parameterFormOpen && parameters.length > 0 && <div className="multi-report-parameter-values">
            <span className="multi-report-section-label">PREVIEW VALUES</span>
            {parameters.map((parameter) => <label key={parameter.name}>{parameter.displayName}
              {parameter.dataType === 'Boolean' ? <select value={parameterValues[parameter.name] ?? parameter.defaultValue ?? ''} onChange={(event) => setParameterValues((current) => ({ ...current, [parameter.name]: event.target.value }))}>
                <option value="">Choose yes or no</option><option value="true">Yes</option><option value="false">No</option>
              </select> : <input
                type={parameter.dataType === 'Integer' || parameter.dataType === 'Decimal' ? 'number' : parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                step={parameter.dataType === 'Decimal' ? 'any' : undefined}
                value={parameterValues[parameter.name] ?? parameter.defaultValue ?? ''}
                onChange={(event) => setParameterValues((current) => ({ ...current, [parameter.name]: event.target.value }))}
              />}
            </label>)}
          </div>}
        </section>
      </div>}

      {detailsDialogOpen && <div className="multi-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setDetailsDialogOpen(false); }}>
        <section className="multi-report-modal" role="dialog" aria-modal="true" aria-labelledby="report-details-title">
          <header><div><span className="login-kicker">REPORT DETAILS</span><h2 id="report-details-title">Edit report name and title</h2></div><button className="icon-button" type="button" aria-label="Close" onClick={() => setDetailsDialogOpen(false)}><Icon name="x" size={17} /></button></header>
          <label>Report name<input value={name} onChange={(event) => setName(event.target.value)} maxLength={250} /></label>
          <label>Page title<input value={title} onChange={(event) => setTitle(event.target.value)} maxLength={250} /></label>
          <label>Dashboard folder preference<select value={category} onChange={(event) => setCategory(event.target.value)}><option value="">Choose when publishing</option>{reportFolders.map((folder) => <option key={folder.name} value={folder.name}>{folder.name}</option>)}</select></label>
          <label>Description<textarea value={description} onChange={(event) => setDescription(event.target.value)} rows={3} maxLength={2000} /></label>
          <footer><button className="button button--secondary" type="button" onClick={() => setDetailsDialogOpen(false)}>Done</button></footer>
        </section>
      </div>}

      {publishDialogOpen && detail && <div className="multi-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !working) setPublishDialogOpen(false); }}>
        <section className="multi-report-modal multi-report-publish-modal" role="dialog" aria-modal="true" aria-labelledby="multi-publish-title">
          <header><div><span className="login-kicker">PUBLISH REPORT</span><h2 id="multi-publish-title">Choose a user dashboard folder</h2></div><button className="icon-button" type="button" aria-label="Close publish options" onClick={() => setPublishDialogOpen(false)} disabled={working}><Icon name="x" size={17} /></button></header>
          <p>{detail.report.name} will appear in the selected section after publishing.</p>
          <ReportFolderPicker
            folders={reportFolders}
            value={publishCategory}
            onChange={setPublishCategory}
            onCreate={createReportFolder}
            onDelete={deleteReportFolder}
            disabled={working || publishFoldersLoading}
          />
          {publishFoldersLoading && <p role="status">Loading dashboard folders…</p>}
          {error && <div className="data-source-alert" role="alert"><Icon name="x" size={16} />{error}</div>}
          {error && <button className="button button--secondary" type="button" onClick={() => void openPublishDialog()} disabled={publishFoldersLoading}>Retry loading folders</button>}
          <footer>
            <button className="button button--secondary" type="button" onClick={() => setPublishDialogOpen(false)} disabled={working}>Cancel</button>
            <button className="button button--primary" type="button" onClick={() => void publish()} disabled={working || publishFoldersLoading || !publishCategory}>
              {working ? <span className="spinner spinner--small" /> : <Icon name="check" size={15} />} Publish to {publishCategory}
            </button>
          </footer>
        </section>
      </div>}

      {deleteDialogOpen && <div className="multi-report-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) setDeleteDialogOpen(false); }}>
        <section className="multi-report-modal multi-report-delete-modal" role="dialog" aria-modal="true" aria-labelledby="delete-report-title">
          <header><div><span className="login-kicker">DELETE REPORT</span><h2 id="delete-report-title">Remove this report?</h2></div><button className="icon-button" type="button" aria-label="Close" onClick={() => setDeleteDialogOpen(false)}><Icon name="x" size={17} /></button></header>
          <p>This removes “{name || 'Untitled report'}” and its versions from the workspace. Published links will no longer work.</p>
          <footer>
            <button className="button button--secondary" type="button" onClick={() => setDeleteDialogOpen(false)} disabled={working}>Keep report</button>
            <button className="button button--primary multi-report-delete-confirm" type="button" onClick={() => void deleteReport()} disabled={working}>
              {working && <span className="spinner spinner--small" />} Delete report
            </button>
          </footer>
        </section>
      </div>}

    </section>
  );
}
