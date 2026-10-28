import { lazy, Suspense, useCallback, useEffect, useState, type ChangeEvent, type DragEvent } from 'react';
import { useAuth } from '../auth/AuthProvider';
import { Icon } from '../components/Icon';
import { ReportFolderPicker, type ReportFolder } from '../components/ReportFolderPicker';
import {
  downloadReportPdf,
  type ReportDownloadComponent,
} from './reportDownloads';
import {
  resolveOrganizationReportHeader,
} from './organizationReportHeader';
import { OrganizationReportHeaderPreview } from './OrganizationReportHeaderPreview';
import {
  normalizePrintHeader,
  type ReportPrintHeader,
} from './reportPrintHeader';
import './ReportsPage.css';

const ReportChartPreview = lazy(() => import('./ReportChartPreview'));
const reportGroups = ['TVS', 'ECS', 'CHILLER'] as const;
type ReportGroup = string;

function reportGroup(value: string | null | undefined): ReportGroup {
  const normalized = value?.trim();
  if (!normalized) return 'Uncategorized';
  return reportGroups.find((group) => group === normalized.toUpperCase()) ?? normalized;
}

interface DataSource {
  dataSourceId: number;
  name: string;
  databaseName: string;
}

interface Dataset {
  datasetId: number;
  dataSourceId: number;
  name: string;
  description: string | null;
}

interface DatasetDetail {
  dataset: Dataset;
  queryDefinition: string;
}

interface ReportField {
  columnIndex: number;
  columnName: string;
  displayName: string;
  width: number;
}

type ChartType = 'Bar' | 'Line' | 'Pie';

interface ReportChart {
  type: ChartType;
  categoryColumnIndex: number;
  valueColumnIndex: number;
}

interface ReportParameter {
  name: string;
  displayName: string;
  dataType: 'String' | 'Integer' | 'Decimal' | 'Boolean' | 'Date' | 'DateTime';
  defaultValue: string | null;
  isRequired: boolean;
  allowMultipleValues: false;
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
  canEdit: boolean;
  canPublish: boolean;
  reportType?: 'SingleSource' | 'MultiSource';
}

interface ReportShareToken {
  token: string;
  versionId: number;
}

interface ReportRole {
  roleId: number;
  name: string;
}

interface ReportVersion {
  versionId: number;
  versionNumber: number;
  status: string;
  isCurrentPublished: boolean;
  createdAt: string;
  changeSummary: string | null;
}

interface ReportDetail {
  report: ReportSummary;
  datasetId: number;
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

interface DatasetColumn {
  name: string;
  dataType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
}

interface ReportPreview {
  fields: ReportField[];
  rows: (string | number | boolean | null)[][];
  truncated: boolean;
  durationMilliseconds: number;
  charts: ReportChart[];
  includeTable: boolean;
  componentOrder?: number[] | null;
}

interface DatasetPreview {
  columns: DatasetColumn[];
  rows: (string | number | boolean | null)[][];
  truncated: boolean;
  durationMilliseconds: number;
}

interface PortableDatasetDefinition {
  originalDatasetId: number;
  alias: string;
  name: string;
  sourceName: string;
}

interface PortablePackageSummary {
  format: string;
  version: number;
  reportType: 'SingleSource' | 'MultiSource';
  name: string;
  datasets: PortableDatasetDefinition[];
  limitations: string[];
}

interface PortableImportResult {
  report: ReportSummary;
  reportType: 'SingleSource' | 'MultiSource';
  warnings: string[];
}

interface ReportPageLayout {
  items: ReportPageItem[];
}

type ReportPageItem =
  | { type: 'table'; rowStart: number; rowEnd: number }
  | { type: 'chart'; chartIndex: number };

const reportPageContentHeight = 980;
const reportTableRowHeight = 34;
const reportTableHeadingHeight = 60;
const reportChartHeight = 350;
const reportFirstPageHeadingHeight = 170;
const reportFollowingPageHeadingHeight = 75;

function getComponentOrder(
  includeTable: boolean,
  charts: ReportChart[],
  savedOrder?: number[] | null,
) {
  const expected = [
    ...(includeTable ? [-1] : []),
    ...charts.map((_, index) => index),
  ];
  if (!savedOrder) return expected;
  const valid = new Set(expected);
  const order = savedOrder.filter((item, index) =>
    valid.has(item) && savedOrder.indexOf(item) === index);
  return [...order, ...expected.filter((item) => !order.includes(item))];
}

function paginateReport(componentOrder: number[], rowCount: number) {
  const pages: ReportPageLayout[] = [{ items: [] }];
  let remainingHeight = reportPageContentHeight - reportFirstPageHeadingHeight;
  let nextRow = 0;

  for (const componentId of componentOrder) {
    if (componentId === -1) {
      do {
        const rowsRemaining = rowCount - nextRow;
        const minimumHeight = reportTableHeadingHeight +
          (rowsRemaining > 0 ? reportTableRowHeight : 0);
        if (remainingHeight < minimumHeight) {
          pages.push({ items: [] });
          remainingHeight = reportPageContentHeight - reportFollowingPageHeadingHeight;
        }
        const rowsAvailable = Math.max(
          0,
          Math.floor((remainingHeight - reportTableHeadingHeight) / reportTableRowHeight),
        );
        const rowsOnPage = Math.min(rowsAvailable, rowsRemaining);
        pages[pages.length - 1].items.push({
          type: 'table',
          rowStart: nextRow,
          rowEnd: nextRow + rowsOnPage,
        });
        nextRow += rowsOnPage;
        remainingHeight -= reportTableHeadingHeight + rowsOnPage * reportTableRowHeight;
      } while (nextRow < rowCount);
      continue;
    }

    if (remainingHeight < reportChartHeight) {
      pages.push({ items: [] });
      remainingHeight = reportPageContentHeight - reportFollowingPageHeadingHeight;
    }
    pages[pages.length - 1].items.push({ type: 'chart', chartIndex: componentId });
    remainingHeight -= reportChartHeight;
  }

  return pages;
}

function getChartPreview(execution: ReportPreview, chart: ReportChart) {
  const categoryIndex = execution.fields.findIndex(
    (field) => field.columnIndex === chart.categoryColumnIndex,
  );
  const valueIndex = execution.fields.findIndex(
    (field) => field.columnIndex === chart.valueColumnIndex,
  );
  const categoryName = execution.fields[categoryIndex]?.displayName ?? 'Category';
  const valueName = execution.fields[valueIndex]?.displayName ?? 'Value';
  const data = execution.rows.flatMap((row) => {
    const rawValue = valueIndex < 0 ? null : row[valueIndex];
    if (rawValue === null || rawValue === undefined || rawValue === '') return [];
    const value = Number(rawValue);
    if (categoryIndex < 0 || !Number.isFinite(value)) return [];
    return [{ category: String(row[categoryIndex] ?? '(blank)'), value }];
  });
  return { data, categoryName, valueName };
}

function isNumericType(dataType: string | undefined) {
  return /^(tinyint|smallint|int|bigint|decimal|numeric|money|smallmoney|float|real)$/i.test(dataType ?? '');
}

function getQueryParameterNames(query: string) {
  const sql = query
    .replace(/'(?:''|[^'])*'/g, ' ')
    .replace(/--[^\r\n]*|\/\*[\s\S]*?\*\//g, ' ')
    .replace(/\[[^\]]*\]|"(?:""|[^"])*"/g, ' ');
  const names = new Set<string>();
  const pattern = /(^|[^@])@([A-Za-z_][A-Za-z0-9_]*)/g;
  let match = pattern.exec(sql);
  while (match) {
    names.add(match[2]);
    match = pattern.exec(sql);
  }
  return [...names];
}

function parameterDisplayName(name: string) {
  return name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_\s]+/g, ' ')
    .replace(/^./, (first) => first.toUpperCase());
}

function parameterInputValue(value: string | null, dataType: ReportParameter['dataType']) {
  if (!value) return '';
  if (dataType === 'Date') return value.slice(0, 10);
  if (dataType === 'DateTime' && /^\d{4}-\d{2}-\d{2}T/.test(value)) return value.slice(0, 16);
  return value;
}

function safeFileName(value: string) {
  return value.trim().replace(/[<>:"/\\|?*\u0000-\u001F]/g, '-').replace(/[. ]+$/, '') || 'report';
}

function inferParameterType(query: string, name: string): ReportParameter['dataType'] {
  const escapedName = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const precedingColumn = new RegExp(
    String.raw`(?:\[([^\]]+)\]|([A-Za-z_][A-Za-z0-9_]*))\s*(?:=|<>|>=|<=|>|<|LIKE)\s*@${escapedName}\b`,
    'i',
  ).exec(query);
  const castType = new RegExp(
    String.raw`CAST\s*\(\s*@${escapedName}\s+AS\s+(date|datetime2?|smalldatetime|time)\b`,
    'i',
  ).exec(query);
  const referencedName = `${precedingColumn?.[1] ?? ''} ${precedingColumn?.[2] ?? ''} ${castType?.[1] ?? ''}`.toLowerCase();
  if (referencedName.includes('datetime') || referencedName.includes('time')) return 'DateTime';
  if (referencedName.includes('date')) return 'Date';
  return 'String';
}

function synchronizeParameters(
  query: string,
  current: ReportParameter[],
  currentValues: Record<string, string>,
) {
  const currentByName = new Map(current.map((parameter) => [parameter.name.toLowerCase(), parameter]));
  const valuesByName = new Map(Object.entries(currentValues).map(([name, value]) => [name.toLowerCase(), value]));
  const names = getQueryParameterNames(query);
  const parameters = names.map((name): ReportParameter => {
    const existing = currentByName.get(name.toLowerCase());
    return existing ?? {
      name,
      displayName: parameterDisplayName(name),
      dataType: inferParameterType(query, name),
      defaultValue: null,
      isRequired: false,
      allowMultipleValues: false,
    };
  });
  const parameterValues = Object.fromEntries(parameters.map((parameter) => [
    parameter.name,
    valuesByName.get(parameter.name.toLowerCase()) ?? parameter.defaultValue ?? '',
  ]));
  return { names, parameters, parameterValues };
}

async function getError(response: Response) {
  try {
    const value: unknown = await response.json();
    if (value && typeof value === 'object' && 'title' in value && typeof value.title === 'string') {
      const traceId = 'traceId' in value && typeof value.traceId === 'string'
        ? ` (Reference ID: ${value.traceId})`
        : '';
      return `${value.title}${traceId}`;
    }
  } catch {
    // A non-JSON error response is reported by status.
  }
  return `The request failed (${response.status}).`;
}

export function ReportsPage({
  initialReportId = null,
  onInitialReportHandled,
  onCreateUnifiedReport,
  onOpenUnifiedReport,
}: {
  initialReportId?: number | null;
  onInitialReportHandled?: () => void;
  onCreateUnifiedReport?: () => void;
  onOpenUnifiedReport?: (reportId: number) => void;
} = {}) {
  const { accessToken, signOut } = useAuth();
  const [reports, setReports] = useState<ReportSummary[]>([]);
  const [reportFolders, setReportFolders] = useState<ReportFolder[]>([]);
  const [datasets, setDatasets] = useState<Dataset[]>([]);
  const [sources, setSources] = useState<DataSource[]>([]);
  const [selectedReport, setSelectedReport] = useState<ReportSummary | null>(null);
  const [versions, setVersions] = useState<ReportVersion[]>([]);
  const [reportName, setReportName] = useState('');
  const [description, setDescription] = useState('');
  const [category, setCategory] = useState('');
  const [datasetId, setDatasetId] = useState('');
  const [title, setTitle] = useState('');
  const [defaultPrintHeader, setDefaultPrintHeader] = useState<ReportPrintHeader | null>(null);
  const [includeTable, setIncludeTable] = useState(true);
  const [charts, setCharts] = useState<ReportChart[]>([]);
  const [componentOrder, setComponentOrder] = useState<number[]>([-1]);
  const [parameters, setParameters] = useState<ReportParameter[]>([]);
  const [expectedParameterNames, setExpectedParameterNames] = useState<string[]>([]);
  const [parameterValues, setParameterValues] = useState<Record<string, string>>({});
  const [availableColumns, setAvailableColumns] = useState<DatasetColumn[]>([]);
  const [roles, setRoles] = useState<ReportRole[]>([]);
  const [shareRoleId, setShareRoleId] = useState('');
  const [shareCanEdit, setShareCanEdit] = useState(false);
  const [shareCanPublish, setShareCanPublish] = useState(false);
  const [shareNotice, setShareNotice] = useState<string | null>(null);
  const [fields, setFields] = useState<ReportField[]>([]);
  const [execution, setExecution] = useState<ReportPreview | null>(null);
  const [columnsLoading, setColumnsLoading] = useState(false);
  const [columnsError, setColumnsError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [reportsFailed, setReportsFailed] = useState(false);
  const [working, setWorking] = useState(false);
  const [openingReportId, setOpeningReportId] = useState<number | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [previewMode, setPreviewMode] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saveNotice, setSaveNotice] = useState<string | null>(null);
  const [publishedConfirmation, setPublishedConfirmation] = useState<{
    reportId: number;
    name: string;
    url: string | null;
    loading: boolean;
    error: string | null;
  } | null>(null);
  const [publishDialogOpen, setPublishDialogOpen] = useState(false);
  const [publishCategory, setPublishCategory] = useState<ReportGroup>('');
  const [publishUrlCopied, setPublishUrlCopied] = useState(false);
  const [portableDialog, setPortableDialog] = useState<'export' | 'import' | null>(null);
  const [publishFoldersLoading, setPublishFoldersLoading] = useState(false);
  const [portableReportId, setPortableReportId] = useState('');
  const [portableFormat, setPortableFormat] = useState<'ersreport' | 'rdl'>('ersreport');
  const [portableContentType, setPortableContentType] = useState<'report' | 'dataset'>('report');
  const [portableDatasetId, setPortableDatasetId] = useState('');
  const [portableDatasetImport, setPortableDatasetImport] = useState(false);
  const [includeDatasetData, setIncludeDatasetData] = useState(false);
  const [allowAnonymousLink, setAllowAnonymousLink] = useState(false);
  const [portableLink, setPortableLink] = useState<string | null>(null);
  const [portableLinkCopied, setPortableLinkCopied] = useState(false);
  const [portableFile, setPortableFile] = useState<File | null>(null);
  const [portablePackage, setPortablePackage] = useState<PortablePackageSummary | null>(null);
  const [datasetMappings, setDatasetMappings] = useState<Record<number, string>>({});

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
    if (!response.ok) throw new Error(await getError(response));
    if (response.status === 204) return undefined as T;
    return response.json() as Promise<T>;
  }, [accessToken, signOut]);

  const loadLibrary = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [reportsResult, headerResult] = await Promise.allSettled([
        request<ReportSummary[]>('/api/reports'),
        request<ReportPrintHeader | null>('/api/reports/organization-header'),
      ]);
      if (reportsResult.status === 'rejected') throw reportsResult.reason;
      setReports(reportsResult.value);
      setReportsFailed(false);
      const warnings: string[] = [];
      if (headerResult.status === 'fulfilled') {
        setDefaultPrintHeader(normalizePrintHeader(
          await resolveOrganizationReportHeader(headerResult.value),
        ));
      } else {
        warnings.push('The organization-wide page header could not be loaded.');
      }
      const [datasetResult, sourceResult] = await Promise.allSettled([
        request<Dataset[]>('/api/datasets'),
        request<DataSource[]>('/api/data-sources'),
      ]);
      if (datasetResult.status === 'fulfilled') {
        setDatasets(datasetResult.value);
      } else {
        warnings.push('Datasets could not be loaded, so report creation is unavailable.');
      }
      if (sourceResult.status === 'fulfilled') {
        setSources(sourceResult.value);
      } else {
        warnings.push('Data-source details could not be loaded.');
      }
      setError(warnings.length > 0 ? warnings.join(' ') : null);
    } catch (cause) {
      setReportsFailed(true);
      setError(cause instanceof Error ? cause.message : 'Reports could not be loaded.');
    } finally {
      setLoading(false);
    }
  }, [request]);

  useEffect(() => {
    void loadLibrary();
  }, [loadLibrary]);

  useEffect(() => {
    if (initialReportId === null || loading) return;
    const report = reports.find((item) => item.reportId === initialReportId);
    onInitialReportHandled?.();
    if (report) {
      if (report.reportType === 'MultiSource') onOpenUnifiedReport?.(report.reportId);
      else void openReport(report);
    } else {
      setError('That report is no longer available in your workspace.');
    }
  }, [initialReportId, loading, onInitialReportHandled, onOpenUnifiedReport, reports]);

  function bindParameters(
    definitions: ReportParameter[],
    values: Record<string, string>,
  ) {
    return definitions.map((parameter) => {
      const value = values[parameter.name] ?? parameter.defaultValue ?? '';
      let boundValue: string | number | boolean | null = value || null;
      if (value && (parameter.dataType === 'Integer' || parameter.dataType === 'Decimal')) {
        boundValue = Number(value);
      } else if (value && parameter.dataType === 'Boolean') {
        boundValue = value === 'true';
      }
      return { name: parameter.name, dataType: parameter.dataType, value: boundValue };
    });
  }

  async function loadDatasetColumns(
    id: number,
    definitions = parameters,
    values = parameterValues,
  ) {
    setColumnsLoading(true);
    setColumnsError(null);
    try {
      const preview = await request<DatasetPreview>(`/api/datasets/${id}/preview`, {
        method: 'POST',
        body: JSON.stringify({ parameters: bindParameters(definitions, values) }),
      });
      setAvailableColumns(preview.columns);
      return preview;
    } catch (cause) {
      setColumnsError(cause instanceof Error ? cause.message : 'Dataset fields could not be loaded.');
      throw cause;
    } finally {
      setColumnsLoading(false);
    }
  }

  function openPortableDialog(mode: 'export' | 'import') {
    setPortableDialog(mode);
    setPortableReportId(reports[0] ? `${reports[0].reportType ?? 'SingleSource'}:${reports[0].reportId}` : '');
    setPortableFormat('ersreport');
    setPortableContentType('report');
    setPortableDatasetId(datasets[0] ? String(datasets[0].datasetId) : '');
    setPortableDatasetImport(false);
    setIncludeDatasetData(false);
    setAllowAnonymousLink(false);
    setPortableLink(null);
    setPortableLinkCopied(false);
    setPortableFile(null);
    setPortablePackage(null);
    setDatasetMappings({});
    setError(null);
  }

  function closePortableDialog() {
    if (working) return;
    setPortableDialog(null);
    setError(null);
  }

  async function exportPortableReport(format: 'ersreport' | 'rdl') {
    const [reportType, reportId] = portableReportId.split(':');
    if (!reportType || !reportId) {
      setError('Choose a report to export.');
      return null;
    }
    setWorking(true);
    setError(null);
    try {
      const query = new URLSearchParams();
      const report = reports.find((item) => item.reportId === Number(reportId) &&
        (item.reportType ?? 'SingleSource') === reportType);
      if (report) query.set('versionId', String(report.versionId));
      if (format === 'ersreport') query.set('includeData', String(includeDatasetData));
      const response = await fetch(
        `/api/portable-reports/${encodeURIComponent(reportType)}/${reportId}/${format === 'rdl' ? 'rdl' : 'export'}?${query}`,
        {
          headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
        },
      );
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await getError(response));
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${safeFileName(report?.name ?? 'report')}.${format}`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report package could not be exported.');
    } finally {
      setWorking(false);
    }
  }

  async function exportPortableDataset() {
    if (!portableDatasetId) {
      setError('Choose a dataset to export.');
      return null;
    }
    const dataset = datasets.find(item => item.datasetId === Number(portableDatasetId));
    setWorking(true);
    setError(null);
    try {
      const response = await fetch(
        `/api/portable-reports/datasets/${portableDatasetId}/export?includeData=${includeDatasetData}`,
        { headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {} },
      );
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await getError(response));
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement('a');
      link.href = url;
      link.download = `${safeFileName(dataset?.name ?? 'dataset')}.ersdataset`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The dataset package could not be exported.');
    } finally {
      setWorking(false);
    }
  }

  async function createPortableLink() {
    const [reportType, reportId] = portableReportId.split(':');
    if (!reportType || !reportId) {
      setError('Choose a report to create a share link.');
      return null;
    }
    setWorking(true);
    setError(null);
    setPortableLink(null);
    setPortableLinkCopied(false);
    try {
      const result = await request<{ url: string }>(
        '/api/portable-reports/export-link',
        {
          method: 'POST',
          body: JSON.stringify({
            reportType,
            reportId: Number(reportId),
            versionId: reports.find((item) => item.reportId === Number(reportId) &&
              (item.reportType ?? 'SingleSource') === reportType)?.versionId ?? null,
            includeData: includeDatasetData,
            allowAnonymous: allowAnonymousLink,
          }),
        },
      );
      setPortableLink(result.url);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'A portable report link could not be created.');
    } finally {
      setWorking(false);
    }
  }

  async function copyPortableLink() {
    if (!portableLink) return;
    try {
      await navigator.clipboard.writeText(portableLink);
      setPortableLinkCopied(true);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The link could not be copied.');
    }
  }

  async function selectPortableFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0] ?? null;
    setError(null);
    setPortableFile(file);
    setPortablePackage(null);
    setPortableDatasetImport(false);
    setDatasetMappings({});
    setPortableLink(null);
    setPortableLinkCopied(false);
    if (!file) return;
    if (file.name.toLowerCase().endsWith('.rdl')) {
      setPortablePackage({
        format: 'rdl',
        version: 1,
        reportType: 'SingleSource',
        name: file.name.replace(/\.rdl$/i, ''),
        datasets: [{ originalDatasetId: 1, alias: 'Main', name: 'Imported RDL dataset', sourceName: '' }],
        limitations: ['The RDL source connection is not imported. Select a permitted SQL data source.'],
      });
      setDatasetMappings({ 1: sources[0] ? String(sources[0].dataSourceId) : '' });
      return null;
    }

    try {
      const parsed: unknown = JSON.parse(await file.text());
      if (parsed && typeof parsed === 'object' && 'format' in parsed &&
          parsed.format === 'ers-portable-dataset' && 'dataset' in parsed &&
          'limitations' in parsed && Array.isArray(parsed.limitations)) {
        const datasetPackage = parsed as {
          format: string;
          version: number;
          dataset: PortableDatasetDefinition & { description?: string | null };
          limitations: string[];
        };
        if (datasetPackage.version !== 1 || !Number.isInteger(datasetPackage.dataset.originalDatasetId) ||
            !datasetPackage.dataset.name || !datasetPackage.dataset.alias) {
          throw new Error('This dataset package is invalid or uses an unsupported format version.');
        }
        setPortableDatasetImport(true);
        setPortablePackage({
          format: datasetPackage.format,
          version: datasetPackage.version,
          reportType: 'SingleSource',
          name: datasetPackage.dataset.name,
          datasets: [datasetPackage.dataset],
          limitations: datasetPackage.limitations,
        });
        setDatasetMappings({
          [datasetPackage.dataset.originalDatasetId]: sources[0] ? String(sources[0].dataSourceId) : '',
        });
        return null;
      }
      if (!parsed || typeof parsed !== 'object' || !('format' in parsed) ||
          !('version' in parsed) || !('reportType' in parsed) || !('name' in parsed) ||
          !('datasets' in parsed) || !Array.isArray(parsed.datasets)) {
        throw new Error('This is not a recognized .ersreport package.');
      }
      const packageValue = parsed as PortablePackageSummary;
      if (packageValue.format !== 'ers-portable-report' || packageValue.version !== 1 ||
          !['SingleSource', 'MultiSource'].includes(packageValue.reportType) ||
          packageValue.datasets.length === 0 ||
          packageValue.datasets.some(dataset => !Number.isInteger(dataset.originalDatasetId))) {
        throw new Error('This report package is invalid or uses an unsupported format version.');
      }
      setPortablePackage(packageValue);
      setPortableDatasetImport(false);
      setDatasetMappings(Object.fromEntries(packageValue.datasets.map(dataset => [
        dataset.originalDatasetId,
        sources[0] ? String(sources[0].dataSourceId) : '',
      ])));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The selected file could not be read.');
      setPortableFile(null);
    }
  }

  async function importPortableReport() {
    if (!portableFile || !portablePackage) {
      setError('Choose a valid .ersreport or .rdl file first.');
      return null;
    }
    if (portablePackage.datasets.some(dataset => !datasetMappings[dataset.originalDatasetId])) {
      setError('Choose a destination data source for every dataset.');
      return null;
    }
    setWorking(true);
    setError(null);
    try {
      const form = new FormData();
      form.append('Package', portableFile);
      form.append('DatasetMappingsJson', JSON.stringify(Object.fromEntries(
        Object.entries(datasetMappings).map(([datasetId, sourceId]) => [datasetId, Number(sourceId)]),
      )));
      if (portableDatasetImport) {
        const dataSourceId = Number(datasetMappings[portablePackage.datasets[0].originalDatasetId]);
        const form = new FormData();
        form.append('Package', portableFile);
        form.append('DataSourceId', String(dataSourceId));
        const response = await fetch('/api/portable-reports/datasets/import', {
          method: 'POST',
          headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
          body: form,
        });
        if (response.status === 401) {
          await signOut();
          throw new Error('Your session has expired. Sign in again.');
        }
        if (!response.ok) throw new Error(await getError(response));
        await response.json();
        setPortableDialog(null);
        await loadLibrary();
        if (portablePackage.limitations.length > 0) {
          setError(portablePackage.limitations.join(' '));
        }
        return;
      }
      const response = await fetch('/api/portable-reports/import', {
        method: 'POST',
        headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
        body: form,
      });
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await getError(response));
      const imported = await response.json() as PortableImportResult;
      setPortableDialog(null);
      await loadLibrary();
      if (imported.reportType === 'MultiSource') {
        onOpenUnifiedReport?.(imported.report.reportId);
      } else {
        await openReport(imported.report);
      }
      if (imported.warnings.length > 0) {
        setError(imported.warnings.join(' '));
      }
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be imported.');
    } finally {
      setWorking(false);
    }
  }

  async function openReport(report: ReportSummary) {
    setSaveNotice(null);
    setOpeningReportId(report.reportId);
    setWorking(true);
    setError(null);
    setExecution(null);
    setExpectedParameterNames([]);
    setAvailableColumns([]);
    setColumnsError(null);
    try {
      const detail = await request<ReportDetail>(`/api/reports/${report.reportId}`);
      setSelectedReport(detail.report);
      setReportName(detail.report.name);
      setDescription(detail.report.description ?? '');
      setCategory(detail.report.category ? reportGroup(detail.report.category) : '');
      setDatasetId(String(detail.datasetId));
      setTitle(detail.design.title);
      const loadedCharts = detail.design.charts?.length
        ? detail.design.charts
        : detail.design.chart ? [detail.design.chart] : [];
      const loadedTable = detail.design.includeTable ?? detail.design.chart === null;
      setIncludeTable(loadedTable);
      setCharts(loadedCharts);
      setComponentOrder(getComponentOrder(
        loadedTable,
        loadedCharts,
        detail.design.componentOrder,
      ));
      setParameters(detail.parameters);
      const valuesByName = new Map(Object.entries(parameterValues).map(([name, value]) => [
        name.toLowerCase(),
        value,
      ]));
      setParameterValues(Object.fromEntries(detail.parameters.map((parameter) => [
        parameter.name,
        valuesByName.get(parameter.name.toLowerCase()) ?? parameter.defaultValue ?? '',
      ])));
      setFields(detail.design.fields);
      setEditorOpen(true);
      setPreviewMode(false);
      const warnings: string[] = [];
      let effectiveParameters = detail.parameters;
      let effectiveValues = Object.fromEntries(
        detail.parameters.map((parameter) => [parameter.name, parameter.defaultValue ?? '']),
      );
      try {
        const dataset = await request<DatasetDetail>(`/api/datasets/${detail.datasetId}`);
        const synchronized = synchronizeParameters(
          dataset.queryDefinition,
          detail.parameters,
          effectiveValues,
        );
        setExpectedParameterNames(synchronized.names);
        setParameters(synchronized.parameters);
        setParameterValues(synchronized.parameterValues);
        effectiveParameters = synchronized.parameters;
        effectiveValues = synchronized.parameterValues;
      } catch (cause) {
        setExpectedParameterNames([]);
        warnings.push(cause instanceof Error
          ? `The report opened, but its dataset definition could not be read: ${cause.message}`
          : 'The report opened, but its dataset definition could not be read.');
      }
      setRoles([]);
      try {
        setVersions(await request<ReportVersion[]>(`/api/reports/${report.reportId}/versions`));
      } catch (cause) {
        setVersions([]);
        warnings.push(cause instanceof Error ? cause.message : 'Version history could not be loaded.');
      }
      try {
        await loadDatasetColumns(
          detail.datasetId,
          effectiveParameters,
          effectiveValues,
        );
      } catch {
        warnings.push('The saved report is open, but dataset fields could not be refreshed. Use Load fields to retry.');
      }
      if (detail.report.canPublish) {
        try {
          setRoles(await request<ReportRole[]>('/api/reports/roles'));
        } catch (cause) {
          warnings.push(cause instanceof Error ? cause.message : 'Role access options could not be loaded.');
        }
      }
      setShareRoleId('');
      setShareCanEdit(false);
      setShareCanPublish(false);
      setShareNotice(null);
      setError(warnings.length > 0 ? warnings.join(' ') : null);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be opened.');
    } finally {
      setOpeningReportId(null);
      setWorking(false);
    }
  }

  async function openVersion(versionId: number) {
    if (!selectedReport || versionId === selectedReport.versionId) return;
    setSaveNotice(null);
    setWorking(true);
    setError(null);
    setExecution(null);
    setExpectedParameterNames([]);
    try {
      const detail = await request<ReportDetail>(
        `/api/reports/${selectedReport.reportId}?versionId=${versionId}`,
      );
      setSelectedReport(detail.report);
      setDatasetId(String(detail.datasetId));
      setTitle(detail.design.title);
      const loadedCharts = detail.design.charts?.length
        ? detail.design.charts
        : detail.design.chart ? [detail.design.chart] : [];
      const loadedTable = detail.design.includeTable ?? detail.design.chart === null;
      setIncludeTable(loadedTable);
      setCharts(loadedCharts);
      setComponentOrder(getComponentOrder(
        loadedTable,
        loadedCharts,
        detail.design.componentOrder,
      ));
      setFields(detail.design.fields);
      setParameters(detail.parameters);
      const defaultValues = Object.fromEntries(detail.parameters.map((parameter) => [
        parameter.name,
        parameter.defaultValue ?? '',
      ]));
      const dataset = await request<DatasetDetail>(`/api/datasets/${detail.datasetId}`);
      const synchronized = synchronizeParameters(dataset.queryDefinition, detail.parameters, defaultValues);
      setExpectedParameterNames(synchronized.names);
      setParameters(synchronized.parameters);
      setParameterValues(synchronized.parameterValues);
      await loadDatasetColumns(detail.datasetId, synchronized.parameters, synchronized.parameterValues);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The selected version could not be loaded.');
    } finally {
      setWorking(false);
    }
  }

  async function changeDataset(event: ChangeEvent<HTMLSelectElement>) {
    const value = event.target.value;
    setDatasetId(value);
    setAvailableColumns([]);
    setColumnsError(null);
    setFields([]);
    setCharts([]);
    setComponentOrder(includeTable ? [-1] : []);
    setExecution(null);
    setParameters([]);
    setParameterValues({});
    setExpectedParameterNames([]);
    if (!value) return;
    setWorking(true);
    setError(null);
    try {
      const dataset = await request<DatasetDetail>(`/api/datasets/${value}`);
      const synchronized = synchronizeParameters(dataset.queryDefinition, [], {});
      setExpectedParameterNames(synchronized.names);
      setParameters(synchronized.parameters);
      setParameterValues(synchronized.parameterValues);
      await loadDatasetColumns(
        Number(value),
        synchronized.parameters,
        synchronized.parameterValues,
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Dataset columns could not be loaded.');
    } finally {
      setWorking(false);
    }
  }

  function toggleColumn(index: number) {
    setSaveNotice(null);
    const column = availableColumns[index];
    if (!column) return;
    const removingField = fields.some((field) => field.columnIndex === index);
    setFields((current) => removingField
      ? current.filter((field) => field.columnIndex !== index)
      : [...current, {
          columnIndex: index,
          columnName: column.name,
          displayName: column.name,
          width: 160,
        }]);
    if (removingField) {
      const removedCharts = charts.flatMap((chart, chartIndex) =>
        chart.categoryColumnIndex === index || chart.valueColumnIndex === index
          ? [chartIndex]
          : []);
      setCharts((current) => current.filter(
        (chart) => chart.categoryColumnIndex !== index && chart.valueColumnIndex !== index,
      ));
      setComponentOrder((current) => current
        .filter((componentId) => !removedCharts.includes(componentId))
        .map((componentId) => componentId < 0
          ? componentId
          : componentId - removedCharts.filter((removedIndex) => removedIndex < componentId).length));
    }
    setExecution(null);
  }

  function updateField(index: number, update: Partial<ReportField>) {
    setSaveNotice(null);
    setFields((current) => current.map((field) =>
      field.columnIndex === index ? { ...field, ...update } : field));
    setExecution(null);
  }

  function addParameter() {
    setSaveNotice(null);
    const usedNames = new Set(parameters.map((parameter) => parameter.name.toLowerCase()));
    let number = parameters.length + 1;
    while (usedNames.has(`parameter${number}`)) number++;
    setParameters((current) => [
      ...current,
      {
        name: `parameter${number}`,
        displayName: 'Parameter',
        dataType: 'String',
        defaultValue: null,
        isRequired: false,
        allowMultipleValues: false,
      },
    ]);
  }

  function updateParameter(index: number, update: Partial<ReportParameter>) {
    setSaveNotice(null);
    const previous = parameters[index];
    if (previous && update.name !== undefined && update.name !== previous.name) {
      setParameterValues((values) => {
        const { [previous.name]: previousValue, ...remaining } = values;
        return { ...remaining, [update.name!]: previousValue ?? '' };
      });
    }
    setParameters((current) => current.map((parameter, parameterIndex) =>
      parameterIndex === index ? { ...parameter, ...update } : parameter));
    setExecution(null);
  }

  function updateRunParameter(name: string, value: string) {
    setSaveNotice(null);
    setParameterValues((current) => ({ ...current, [name]: value }));
    setExecution(null);
  }

  function parameterNamesMatchDataset() {
    const actual = parameters.map((parameter) => parameter.name.trim().toLowerCase()).sort();
    const expected = expectedParameterNames.map((name) => name.toLowerCase()).sort();
    return actual.length === expected.length && actual.every((name, index) => name === expected[index]);
  }

  function synchronizeFromDataset() {
    const selectedId = Number(datasetId);
    if (!selectedId) return;
    setWorking(true);
    setError(null);
    void request<DatasetDetail>(`/api/datasets/${selectedId}`)
      .then((detail) => {
        const synchronized = synchronizeParameters(
          detail.queryDefinition,
          parameters,
          parameterValues,
        );
        setExpectedParameterNames(synchronized.names);
        setParameters(synchronized.parameters);
        setParameterValues(synchronized.parameterValues);
      })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Dataset parameters could not be loaded.');
      })
      .finally(() => setWorking(false));
  }

  function moveField(index: number, direction: -1 | 1) {
    setSaveNotice(null);
    setFields((current) => {
      const position = current.findIndex((field) => field.columnIndex === index);
      const nextPosition = position + direction;
      if (position < 0 || nextPosition < 0 || nextPosition >= current.length) return current;
      const next = [...current];
      [next[position], next[nextPosition]] = [next[nextPosition], next[position]];
      return next;
    });
    setExecution(null);
  }

  function addChart(type: ChartType) {
    setSaveNotice(null);
    const numericField = fields.find((field) =>
      isNumericType(availableColumns[field.columnIndex]?.dataType));
    const categoryField = fields.find((field) =>
      field.columnIndex !== numericField?.columnIndex);
    if (!categoryField || !numericField) {
      setError('Select at least one category column and one numeric column before adding a chart.');
      return;
    }
    setCharts((current) => [
      ...current,
      {
        type,
        categoryColumnIndex: categoryField.columnIndex,
        valueColumnIndex: numericField.columnIndex,
      },
    ]);
    setComponentOrder((current) => [...current, charts.length]);
    setExecution(null);
  }

  function removeChart(index: number) {
    setSaveNotice(null);
    setCharts((current) => current.filter((_, chartIndex) => chartIndex !== index));
    setComponentOrder((current) => current
      .filter((componentId) => componentId !== index)
      .map((componentId) => componentId > index ? componentId - 1 : componentId));
    setExecution(null);
  }

  function moveComponent(componentId: number, direction: -1 | 1) {
    setSaveNotice(null);
    setComponentOrder((current) => {
      const order = getComponentOrder(includeTable, charts, current);
      const position = order.indexOf(componentId);
      const nextPosition = position + direction;
      if (position < 0 || nextPosition < 0 || nextPosition >= order.length) return order;
      [order[position], order[nextPosition]] = [order[nextPosition], order[position]];
      return order;
    });
    setExecution(null);
  }

  function reorderComponent(dragEvent: DragEvent<HTMLElement>, targetId: number) {
    dragEvent.preventDefault();
    setSaveNotice(null);
    const draggedId = Number(dragEvent.dataTransfer.getData('text/plain'));
    if (!Number.isInteger(draggedId) || draggedId === targetId) return;
    setComponentOrder((current) => {
      const order = getComponentOrder(includeTable, charts, current);
      const sourceIndex = order.indexOf(draggedId);
      const targetIndex = order.indexOf(targetId);
      if (sourceIndex < 0 || targetIndex < 0) return order;
      order.splice(sourceIndex, 1);
      order.splice(targetIndex, 0, draggedId);
      return order;
    });
    setExecution(null);
  }

  function updateChart(index: number, update: Partial<ReportChart>) {
    setSaveNotice(null);
    setCharts((current) => current.map((chart, chartIndex) =>
      chartIndex === index ? { ...chart, ...update } : chart));
    setExecution(null);
  }

  async function saveDraft() {
    if (!datasetId) return null;
    setSaveNotice(null);
    if (!parameterNamesMatchDataset()) {
      setError(`Report parameters must match the selected dataset query: ${expectedParameterNames.map((name) => `@${name}`).join(', ') || 'no SQL parameters'}. Use "Sync from dataset" to align them.`);
      return null;
    }
    const parameterNames = new Set<string>();
    for (const parameter of parameters) {
      const name = parameter.name.trim();
      if (!/^[A-Za-z_][A-Za-z0-9_]{0,127}$/.test(name) ||
          parameterNames.has(name.toLowerCase())) {
        setError('Parameter names must be unique SQL identifiers without the @ prefix.');
        return null;
      }
      if (!parameter.displayName.trim()) {
        setError('Enter a prompt label for each report parameter.');
        return null;
      }
      parameterNames.add(name.toLowerCase());
    }
    setWorking(true);
    setError(null);
    setExecution(null);
    const body = JSON.stringify({
      name: reportName,
      description: description || null,
      category: category || null,
      datasetId: Number(datasetId),
      design: {
        title: title || reportName,
        fields,
        includeTable,
        chart: charts[0] ?? null,
        charts,
        componentOrder: getComponentOrder(includeTable, charts, componentOrder),
        printHeader: null,
      },
      parameters,
    });
    try {
      const detail = selectedReport
        ? await request<ReportDetail>(`/api/reports/${selectedReport.reportId}`, { method: 'PUT', body })
        : await request<ReportDetail>('/api/reports', { method: 'POST', body });
      setSelectedReport(detail.report);
      setTitle(detail.design.title);
      setFields(detail.design.fields);
      const loadedCharts = detail.design.charts?.length
        ? detail.design.charts
        : detail.design.chart ? [detail.design.chart] : [];
      const loadedTable = detail.design.includeTable ?? detail.design.chart === null;
      setIncludeTable(loadedTable);
      setCharts(loadedCharts);
      setComponentOrder(getComponentOrder(
        loadedTable,
        loadedCharts,
        detail.design.componentOrder,
      ));
      setParameters(detail.parameters);
      setParameterValues(Object.fromEntries(
        detail.parameters.map((parameter) => [parameter.name, parameter.defaultValue ?? '']),
      ));
      setEditorOpen(true);
      const warnings: string[] = [];
      try {
        setVersions(await request<ReportVersion[]>(
          `/api/reports/${detail.report.reportId}/versions`,
        ));
      } catch (cause) {
        warnings.push(cause instanceof Error
          ? `Draft saved, but version history could not be refreshed: ${cause.message}`
          : 'Draft saved, but version history could not be refreshed.');
      }
      if (detail.report.canPublish) {
        try {
          setRoles(await request<ReportRole[]>('/api/reports/roles'));
        } catch (cause) {
          warnings.push(cause instanceof Error
            ? `Draft saved, but publishing options could not be refreshed: ${cause.message}`
            : 'Draft saved, but publishing options could not be refreshed.');
        }
      }
      setShareRoleId('');
      setShareCanEdit(false);
      setShareCanPublish(false);
      setShareNotice(null);
      await loadLibrary();
      setSaveNotice(
        `Draft saved as v${detail.report.versionNumber}.${warnings.length > 0 ? ` ${warnings.join(' ')}` : ' You can return to it later or publish when ready.'}`,
      );
      return detail;
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report draft could not be saved.');
      return null;
    } finally {
      setWorking(false);
    }
  }

  async function publishDraft() {
    if (!selectedReport || selectedReport.versionStatus !== 'Draft') return;
    setWorking(true);
    setError(null);
    setShareNotice(null);
    try {
      const versionId = selectedReport.versionId;
      const published = await request<ReportSummary>(
        `/api/reports/${selectedReport.reportId}/versions/${versionId}/publish`,
        { method: 'POST', body: JSON.stringify({ category: publishCategory }) },
      );
      setPublishDialogOpen(false);
      setPublishedConfirmation({
        reportId: published.reportId,
        name: published.name,
        url: null,
        loading: true,
        error: null,
      });
      setPublishUrlCopied(false);
      await loadLibrary();
      const detail = await request<ReportDetail>(
        `/api/reports/${selectedReport.reportId}?versionId=${versionId}`,
      );
      setSelectedReport(detail.report);
      setVersions(await request<ReportVersion[]>(
        `/api/reports/${selectedReport.reportId}/versions`,
      ));
      try {
        const link = await request<ReportShareToken>(
          `/api/reports/${published.reportId}/share-token`,
        );
        setPublishedConfirmation({
          reportId: published.reportId,
          name: published.name,
          url: `${window.location.origin}/shared/${encodeURIComponent(link.token)}`,
          loading: false,
          error: null,
        });
      } catch (cause) {
        setPublishedConfirmation({
          reportId: published.reportId,
          name: published.name,
          url: null,
          loading: false,
          error: cause instanceof Error
            ? `Published successfully, but its share link could not be loaded: ${cause.message}`
            : 'Published successfully, but its share link could not be loaded.',
        });
      }

    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The report could not be published.');
    } finally {
      setWorking(false);
    }
  }

  async function openPublishDialog() {
    if (!selectedReport) return;
    setPublishDialogOpen(true);
    setError(null);
    setPublishFoldersLoading(true);
    try {
      const folders = await request<ReportFolder[]>('/api/reports/folders');
      setReportFolders(folders);
      const preferred = folders.find((folder) =>
        folder.name.localeCompare(selectedReport.category ?? '', undefined, { sensitivity: 'accent' }) === 0);
      setPublishCategory(preferred?.name ?? folders[0]?.name ?? '');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Dashboard folders could not be loaded.');
    } finally {
      setPublishFoldersLoading(false);
    }
  }

  async function createReportFolder(name: string) {
    const folder = await request<ReportFolder>('/api/reports/folders', {
      method: 'POST',
      body: JSON.stringify({ name }),
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

  async function copyPublishedUrl() {
    if (!publishedConfirmation?.url) return;
    try {
      await navigator.clipboard.writeText(publishedConfirmation.url);
      setPublishUrlCopied(true);
      setPublishedConfirmation((current) => current ? { ...current, error: null } : current);
    } catch {
      setPublishedConfirmation((current) => current ? {
        ...current,
        error: 'Clipboard access was blocked. Select and copy the URL from the field.',
      } : current);
    }
  }

  async function shareWithRole() {
    if (!selectedReport || !shareRoleId) return;
    setWorking(true);
    setError(null);
    setShareNotice(null);
    try {
      await request<void>(
        `/api/reports/${selectedReport.reportId}/permissions/roles/${shareRoleId}`,
        {
          method: 'PUT',
          body: JSON.stringify({
            canView: true,
            canEdit: shareCanEdit,
            canPublish: shareCanPublish,
          }),
        },
      );
      setShareNotice('Role access saved. This role can view current published report versions.');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Report access could not be updated.');
    } finally {
      setWorking(false);
    }
  }

  async function runReport() {
    if (!datasetId || fields.length === 0) return false;
    if (!parameterNamesMatchDataset()) {
      setError(`Report parameters must match the selected dataset query: ${expectedParameterNames.map((name) => `@${name}`).join(', ') || 'no SQL parameters'}. Use "Sync from dataset" to align them.`);
      return false;
    }
    const missingParameter = parameters.find((parameter) =>
      parameter.isRequired &&
      !(parameterValues[parameter.name] ?? parameter.defaultValue ?? '').trim());
    if (missingParameter) {
      setError(`Enter a value for "${missingParameter.displayName}" before previewing.`);
      return false;
    }
    setWorking(true);
    setError(null);
    setExecution(null);
    try {
      const preview = await request<DatasetPreview>(`/api/datasets/${datasetId}/preview`, {
        method: 'POST',
        body: JSON.stringify({ parameters: bindParameters(parameters, parameterValues) }),
      });
      const selectedFields = [...fields];
      setExecution({
        fields: selectedFields,
        rows: preview.rows.map((row) =>
          selectedFields.map((field) => row[field.columnIndex] ?? null)),
        truncated: preview.truncated,
        durationMilliseconds: preview.durationMilliseconds,
        charts,
        includeTable,
        componentOrder: getComponentOrder(includeTable, charts, componentOrder),
      });
      return true;
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The current report design could not be previewed.');
      return false;
    } finally {
      setWorking(false);
    }
  }

  async function previewReport() {
    setError(null);
    if (await runReport()) setPreviewMode(true);
  }

  async function downloadPreviewPdf() {
    if (!execution) return;
    setWorking(true);
    setError(null);
    try {
      const chartsToExport = execution.charts;
      const components: ReportDownloadComponent[] = [];
      for (const componentId of getComponentOrder(
        execution.includeTable,
        chartsToExport,
        execution.componentOrder ?? componentOrder,
      )) {
        if (componentId === -1 && execution.includeTable) {
          components.push({
            title: 'Details',
            type: 'Table',
            columns: execution.fields.map((field) => field.displayName),
            rows: execution.rows,
          });
          continue;
        }

        const chart = chartsToExport[componentId];
        if (!chart) continue;
        const preview = getChartPreview(execution, chart);
        components.push({
          title: `${chart.type} chart`,
          type: chart.type,
          columns: [preview.categoryName, preview.valueName],
          rows: preview.data.map((point) => [point.category, point.value]),
        });
      }
      await downloadReportPdf(
        selectedReport?.name ?? reportName,
        title || selectedReport?.name || reportName,
        category || selectedReport?.category || 'REPORT',
        components,
        execution.truncated,
        effectivePrintHeader,
        {
          parameters: parameters.map((parameter) => ({
            name: parameter.displayName,
            value: parameterPayload()[parameter.name],
          })),
          publishedBy: selectedReport?.publishedBy,
          publishedAt: selectedReport?.publishedAt,
          versionNumber: selectedReport?.versionNumber,
        },
      );
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The PDF download could not be created.');
    } finally {
      setWorking(false);
    }
  }

  function parameterPayload(): Record<string, string | number | boolean | null> {
    return Object.fromEntries(bindParameters(parameters, parameterValues)
      .map((parameter) => [parameter.name, parameter.value]));
  }

  async function exportCsv() {
    if (!selectedReport || !accessToken) return;
    setWorking(true);
    setError(null);
    try {
      const response = await fetch(
        `/api/reports/${selectedReport.reportId}/export.csv`,
        {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${accessToken}`,
            'Content-Type': 'application/json',
          },
          body: JSON.stringify({
            versionId: selectedReport.versionId,
            parameters: parameterPayload(),
          }),
        },
      );
      if (response.status === 401) {
        await signOut();
        throw new Error('Your session has expired. Sign in again.');
      }
      if (!response.ok) throw new Error(await getError(response));
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${selectedReport.name.replace(/[<>:"/\\|?*]/g, '_')}.csv`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The CSV export failed.');
    } finally {
      setWorking(false);
    }
  }

  function closeEditor() {
    setEditorOpen(false);
    setSelectedReport(null);
    setVersions([]);
    setExecution(null);
    setPreviewMode(false);
    setError(null);
    setSaveNotice(null);
  }

  const selectedDataset = datasets.find((item) => item.datasetId === Number(datasetId));
  const selectedSource = sources.find((item) => item.dataSourceId === selectedDataset?.dataSourceId);
  const canEditReport = !selectedReport || selectedReport.canEdit;
  const selectedFields = fields;
  const numericFields = fields.filter((field) =>
    isNumericType(availableColumns[field.columnIndex]?.dataType));
  const orderedComponents = getComponentOrder(
    execution?.includeTable ?? includeTable,
    execution?.charts ?? charts,
    execution?.componentOrder ?? componentOrder,
  );
  const reportPages = paginateReport(orderedComponents, execution?.rows.length ?? 0);
  const effectivePrintHeader = defaultPrintHeader;

  return (
    <section className="reports-page" aria-labelledby="reports-title">
      {!editorOpen ? (
        <>
          <div className="reports-heading">
            <div><span className="login-kicker">REPORTING</span><h1 id="reports-title">Report library</h1><p>Open saved reports or create one with the unified report designer.</p></div>
            <div className="reports-heading-actions">
              <button className="data-source-refresh" type="button" onClick={() => void loadLibrary()} disabled={loading}>
                <Icon name="activity" size={16} /> Refresh
              </button>
              <button className="button button--secondary" type="button" onClick={() => openPortableDialog('import')} disabled={working}>
                <Icon name="file" size={16} /> Import
              </button>
              <button className="button button--secondary" type="button" onClick={() => openPortableDialog('export')} disabled={(reports.length === 0 && datasets.length === 0) || working}>
                <Icon name="file" size={16} /> Export
              </button>
              <button className="button button--primary" type="button" onClick={onCreateUnifiedReport} disabled={datasets.length === 0}>
                <Icon name="plus" size={17} /> New report
              </button>
            </div>
          </div>
          {error && <div className="data-source-alert" role="alert"><Icon name="x" size={17} /><span>{error}</span></div>}
          {loading ? (
            <div className="data-source-empty"><span className="spinner spinner--large" /><p>Loading reports…</p></div>
          ) : reportsFailed ? (
            <div className="data-source-empty">
              <span className="data-source-empty-icon"><Icon name="activity" size={22} /></span>
              <h3>Reports are unavailable</h3>
              <p>Refresh to retry loading your report library.</p>
              <button className="button button--secondary" type="button" onClick={() => void loadLibrary()}>Retry</button>
            </div>
          ) : reports.length === 0 ? (
            <div className="data-source-empty">
              <span className="data-source-empty-icon"><Icon name="file" size={22} /></span>
              <h3>No reports yet</h3>
              <p>{datasets.length ? 'Create a report using an accessible dataset.' : 'Create a dataset first, then use it to design your first report.'}</p>
              {datasets.length > 0 && <button className="button button--secondary" type="button" onClick={onCreateUnifiedReport}>Start a report</button>}
            </div>
          ) : (
            <div className="reports-library">
              {reports.map((report) => (
                <article className="report-library-card" key={report.reportId}>
                  <span className="report-library-icon"><Icon name="file" size={19} /></span>
                  <div className="report-library-copy">
                    <strong>{report.name}</strong>
                    <span>{report.description || report.category || 'No description'}</span>
                    <small>Version {report.versionNumber} · {new Date(report.updatedAt).toLocaleDateString()}</small>
                  </div>
                  <span className={`report-status${report.isCurrentPublished ? ' is-published' : ''}`}>
                    <span />{report.isCurrentPublished ? 'Published' : report.versionStatus}
                  </span>
                  <button
                    className="button button--secondary"
                    type="button"
                    onClick={() => report.reportType === 'MultiSource'
                      ? onOpenUnifiedReport?.(report.reportId)
                      : void openReport(report)}
                    disabled={working}
                  >
                    {openingReportId === report.reportId && <span className="spinner spinner--small" />}
                    Open
                  </button>
                </article>
              ))}
            </div>
          )}
        </>
      ) : (
        <>
          <div className="report-editor-topbar">
            <button type="button" className="report-back" onClick={closeEditor}><Icon name="chevron" size={16} /> All reports</button>
            <div className="report-editor-actions">
              {selectedReport && <span className={`report-status${selectedReport.isCurrentPublished ? ' is-published' : ''}`}><span />{selectedReport.versionStatus} · v{selectedReport.versionNumber}</span>}
              {previewMode ? (
                <>
                  <button className="button button--secondary" type="button" onClick={() => setPreviewMode(false)}>
                    <Icon name="activity" size={15} /> Back to designer
                  </button>
                  <button className="button button--primary" type="button" onClick={() => void downloadPreviewPdf()} disabled={working}>
                    <Icon name="arrow" size={15} /> Download PDF
                  </button>
                </>
              ) : (
                <>
                  <button className="button button--secondary" type="button" onClick={() => void previewReport()} disabled={!datasetId || fields.length === 0 || working || !parameterNamesMatchDataset()}>
                    <Icon name="eye" size={15} /> Preview report
                  </button>
                  {selectedReport?.versionStatus === 'Draft' && selectedReport.canPublish && (
                    <button className="button button--secondary" type="button" onClick={() => void openPublishDialog()} disabled={working}>
                      <Icon name="check" size={15} /> Publish
                    </button>
                  )}
                  {selectedReport && selectedReport.isCurrentPublished && (
                    <button className="button button--secondary" type="button" onClick={() => void exportCsv()} disabled={working}>
                      <Icon name="arrow" size={15} /> Export CSV
                    </button>
                  )}
                  {canEditReport && (
                    <button className="button button--primary" type="button" onClick={() => void saveDraft()} disabled={working || fields.length === 0 || !reportName.trim() || (!includeTable && charts.length === 0)}>
                      {working && <span className="spinner spinner--small" />}
                      {selectedReport ? 'Save draft' : 'Create draft'}
                    </button>
                  )}
                </>
              )}
            </div>
          </div>
          {selectedReport && versions.length > 1 && (
            <div className="report-version-history">
              <div>
                <strong>Version history</strong>
                <span>{selectedReport.canEdit ? 'Review earlier versions or use one as the starting point for a new draft.' : 'Available published version.'}</span>
              </div>
              <label className="report-version-select">
                <span>Version</span>
                <select
                  value={selectedReport.versionId}
                  disabled={working}
                  onChange={(event) => void openVersion(Number(event.target.value))}
                >
                  {versions.map((version) => (
                    <option key={version.versionId} value={version.versionId}>
                      v{version.versionNumber} · {version.status}
                      {version.isCurrentPublished ? ' · current published' : ''}
                    </option>
                  ))}
                </select>
              </label>
            </div>
          )}
          {error && <div className="data-source-alert" role="alert"><Icon name="x" size={17} /><span>{error}</span><button type="button" onClick={() => setError(null)} aria-label="Dismiss error"><Icon name="x" size={16} /></button></div>}
          {saveNotice && <div className="report-save-notice" role="status"><Icon name="check" size={16} /><span>{saveNotice}</span></div>}

          <div
            className={`report-editor${previewMode ? ' is-previewing' : ''}`}
            onChange={() => setSaveNotice(null)}
          >
            <aside className="report-properties">
              <div className="report-properties-heading"><span className="panel-overline">REPORT DESIGN</span><h2>Properties</h2></div>
              <label className="data-source-field"><span>Report name</span><input value={reportName} maxLength={250} disabled={!canEditReport} onChange={(event) => setReportName(event.target.value)} /></label>
              <label className="data-source-field"><span>Title on report</span><input value={title} maxLength={250} disabled={!canEditReport} onChange={(event) => setTitle(event.target.value)} placeholder="Report title" /></label>
              <label className="data-source-field"><span>Description</span><textarea value={description} maxLength={2000} disabled={!canEditReport} onChange={(event) => setDescription(event.target.value)} rows={3} /></label>
              <label className="data-source-field"><span>Dashboard folder preference</span><select value={category} disabled={!canEditReport} onChange={(event) => setCategory(event.target.value)}><option value="">Choose when publishing</option>{reportFolders.map((folder) => <option key={folder.name} value={folder.name}>{folder.name}</option>)}</select><small>You can confirm or change this when publishing.</small></label>
              <label className="data-source-field"><span>Dataset</span>
                <select value={datasetId} disabled={!canEditReport || working} onChange={(event) => void changeDataset(event)}>
                  <option value="">Choose a dataset</option>
                  {datasets.map((dataset) => <option key={dataset.datasetId} value={dataset.datasetId}>{dataset.name}</option>)}
                </select>
              </label>
              {selectedDataset && <div className="report-data-source-note"><Icon name="database" size={15} /><span>{selectedSource?.name ?? 'Source'}<small>{selectedSource?.databaseName ?? ''}</small></span></div>}
              <div className="report-field-editor report-parameters-editor">
                <div className="report-field-editor-heading">
                  <strong>Query parameters</strong>
                  {datasetId && <button className="data-source-refresh" type="button" disabled={working} onClick={synchronizeFromDataset}>Sync from dataset</button>}
                  {canEditReport && <button className="data-source-refresh" type="button" onClick={addParameter}>Add parameter</button>}
                </div>
                {datasetId && (
                  <p className={parameterNamesMatchDataset() ? undefined : 'report-column-error'} role={parameterNamesMatchDataset() ? undefined : 'alert'}>
                    {expectedParameterNames.length === 0
                      ? 'The selected dataset query has no named parameters.'
                      : `Dataset query expects: ${expectedParameterNames.map((name) => `@${name}`).join(', ')}.`}
                    {' '}SQL names must match exactly; prompt labels can be customized separately.
                  </p>
                )}
                {parameters.length === 0 ? (
                  expectedParameterNames.length === 0
                    ? <p>Add a parameter when the dataset query contains a named SQL parameter such as @startDate.</p>
                    : <p>Sync from dataset to create the required parameter definitions.</p>
                ) : parameters.map((parameter, index) => (
                  <div className="report-parameter-definition" key={`${index}-${parameter.name}`}>
                    <label className="data-source-field">
                      <span>SQL name (without @)</span>
                      <input value={parameter.name} maxLength={128} placeholder="startDate" disabled={!canEditReport} onChange={(event) => updateParameter(index, { name: event.target.value })} />
                    </label>
                    <label className="data-source-field">
                      <span>Prompt label</span>
                      <input value={parameter.displayName} maxLength={200} disabled={!canEditReport} onChange={(event) => updateParameter(index, { displayName: event.target.value })} />
                    </label>
                    <label className="data-source-field">
                      <span>Type</span>
                      <select value={parameter.dataType} disabled={!canEditReport} onChange={(event) => updateParameter(index, { dataType: event.target.value as ReportParameter['dataType'] })}>
                        <option value="String">Text</option>
                        <option value="Integer">Integer</option>
                        <option value="Decimal">Decimal</option>
                        <option value="Boolean">Yes / no</option>
                        <option value="Date">Date</option>
                        <option value="DateTime">Date and time</option>
                      </select>
                    </label>
                    <label className="data-source-field">
                      <span>Default value</span>
                      <input
                        type={parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                        value={parameterInputValue(parameter.defaultValue, parameter.dataType)}
                        disabled={!canEditReport}
                        onChange={(event) => updateParameter(index, { defaultValue: event.target.value || null })}
                      />
                    </label>
                    <label className="report-share-checkbox">
                      <input type="checkbox" checked={parameter.isRequired} disabled={!canEditReport} onChange={(event) => updateParameter(index, { isRequired: event.target.checked })} />
                      Required
                    </label>
                    {canEditReport && <button type="button" className="report-parameter-remove" onClick={() => setParameters((current) => current.filter((_, itemIndex) => itemIndex !== index))}>Remove</button>}
                  </div>
                ))}
              </div>
              {parameters.length > 0 && (
                <div className="report-field-editor report-parameter-values">
                  <div className="report-field-editor-heading"><strong>Run values</strong><span>Used for preview and export</span></div>
                  {parameters.map((parameter) => (
                    <label className="data-source-field" key={parameter.name}>
                      <span>{parameter.displayName}{parameter.isRequired ? ' *' : ''}</span>
                      {parameter.dataType === 'Boolean' ? (
                        <select value={parameterValues[parameter.name] ?? parameter.defaultValue ?? ''} onChange={(event) => updateRunParameter(parameter.name, event.target.value)}>
                          <option value="">Choose yes or no</option>
                          <option value="true">Yes</option>
                          <option value="false">No</option>
                        </select>
                      ) : (
                        <input
                          type={parameter.dataType === 'Integer' || parameter.dataType === 'Decimal' ? 'number' : parameter.dataType === 'Date' ? 'date' : parameter.dataType === 'DateTime' ? 'datetime-local' : 'text'}
                          step={parameter.dataType === 'Decimal' ? 'any' : undefined}
                          value={parameterInputValue(
                            parameterValues[parameter.name] ?? parameter.defaultValue,
                            parameter.dataType,
                          )}
                          required={parameter.isRequired}
                          onChange={(event) => updateRunParameter(parameter.name, event.target.value)}
                        />
                      )}
                    </label>
                  ))}
                </div>
              )}
              <div className="report-field-editor">
                <div className="report-field-editor-heading">
                  <strong>Table columns</strong>
                  <button
                    className="data-source-refresh"
                    type="button"
                    disabled={!datasetId || columnsLoading || working}
                    onClick={() => {
                      setWorking(true);
                      setError(null);
                      void loadDatasetColumns(Number(datasetId)).catch((cause: unknown) => {
                        setError(cause instanceof Error ? cause.message : 'Dataset columns could not be loaded.');
                      }).finally(() => setWorking(false));
                    }}
                  >
                    Load fields
                  </button>
                  <span>{fields.length} selected</span>
                </div>
                {!datasetId ? <p>Choose a dataset to configure the report table.</p> : columnsLoading ? <p>Loading dataset columns…</p> : availableColumns.length === 0 ? <p className={columnsError ? 'report-column-error' : undefined} role={columnsError ? 'alert' : undefined}>{columnsError || 'No fields were returned by the selected dataset.'}</p> : (
                  <div className="report-column-options">
                    {availableColumns.map((column, index) => {
                      const field = fields.find((item) => item.columnIndex === index);
                      return (
                        <div className={`report-column-option${field ? ' is-selected' : ''}`} key={`${index}-${column.name}`}>
                          <label><input type="checkbox" checked={Boolean(field)} disabled={!canEditReport} onChange={() => toggleColumn(index)} /><span><strong>{column.name}</strong><small>{column.dataType}</small></span></label>
                          {field && (
                            <div className="report-column-settings">
                              <input aria-label={`${column.name} display name`} value={field.displayName} disabled={!canEditReport} onChange={(event) => updateField(index, { displayName: event.target.value })} />
                              <input
                                aria-label={`${column.name} width`}
                                type="number"
                                min={60}
                                max={800}
                                value={field.width}
                                disabled={!canEditReport}
                                onChange={(event) => {
                                  const width = Number(event.target.value);
                                  if (Number.isFinite(width)) {
                                    updateField(index, { width: Math.min(800, Math.max(60, Math.round(width))) });
                                  }
                                }}
                              />
                              <button type="button" aria-label={`Move ${column.name} up`} disabled={!canEditReport || fields[0]?.columnIndex === index} onClick={() => moveField(index, -1)}>↑</button>
                              <button type="button" aria-label={`Move ${column.name} down`} disabled={!canEditReport || fields[fields.length - 1]?.columnIndex === index} onClick={() => moveField(index, 1)}>↓</button>
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
              <div className="report-field-editor report-visualization-editor">
                <div className="report-field-editor-heading"><strong>Report components</strong><span>{(includeTable ? 1 : 0) + charts.length} on report</span></div>
                <label className="report-share-checkbox report-table-toggle">
                  <input type="checkbox" checked={includeTable} disabled={!canEditReport} onChange={(event) => {
                    const checked = event.target.checked;
                    setIncludeTable(checked);
                    setComponentOrder((current) => checked
                      ? [...getComponentOrder(false, charts, current), -1]
                      : current.filter((componentId) => componentId !== -1));
                    setExecution(null);
                  }} />
                  Include data table
                </label>
                <div className="report-chart-actions" aria-label="Add a chart">
                  {(['Bar', 'Line', 'Pie'] as const).map((type) => (
                    <button
                      key={type}
                      className="button button--secondary"
                      type="button"
                      disabled={!canEditReport || columnsLoading || selectedFields.length < 2 || numericFields.length === 0 || charts.length >= 10}
                      onClick={() => addChart(type)}
                    >
                      Add {type.toLowerCase()} chart
                    </button>
                  ))}
                </div>
                <div className="report-component-order" aria-label="Report component order">
                  <span className="report-component-order-hint">Drag components or use arrows to reorder</span>
                  {orderedComponents.map((componentId, position) => {
                    const componentName = componentId === -1
                      ? 'Data table'
                      : `${charts[componentId]?.type ?? 'Chart'} chart ${componentId + 1}`;
                    return (
                      <div
                        className="report-component-order-item"
                        key={componentId}
                        draggable={canEditReport}
                        onDragStart={(event) => {
                          event.dataTransfer.setData('text/plain', String(componentId));
                          event.dataTransfer.effectAllowed = 'move';
                        }}
                        onDragOver={(event) => event.preventDefault()}
                        onDrop={(event) => reorderComponent(event, componentId)}
                      >
                        <span>{position + 1}. {componentName}</span>
                        <div>
                          <button
                            type="button"
                            aria-label={`Move ${componentName} up`}
                            disabled={!canEditReport || position === 0}
                            onClick={() => moveComponent(componentId, -1)}
                          >↑</button>
                          <button
                            type="button"
                            aria-label={`Move ${componentName} down`}
                            disabled={!canEditReport || position === orderedComponents.length - 1}
                            onClick={() => moveComponent(componentId, 1)}
                          >↓</button>
                        </div>
                      </div>
                    );
                  })}
                </div>
                {charts.length === 0 && (selectedFields.length < 2 || numericFields.length === 0) && (
                  <p>Select at least one category and one numeric field to add charts.</p>
                )}
                {charts.map((chart, index) => (
                  <div className="report-chart-definition" key={`${chart.type}-${index}`}>
                    <div className="report-field-editor-heading">
                      <strong>{chart.type} chart {index + 1}</strong>
                      {canEditReport && <button className="report-parameter-remove" type="button" onClick={() => {
                        removeChart(index);
                      }}>Remove</button>}
                    </div>
                    <label className="data-source-field">
                      <span>Category column</span>
                      <select
                        value={chart.categoryColumnIndex}
                        disabled={!canEditReport}
                        onChange={(event) => updateChart(index, { categoryColumnIndex: Number(event.target.value) })}
                      >
                        {selectedFields.filter((field) => field.columnIndex !== chart.valueColumnIndex).map((field) => (
                          <option key={field.columnIndex} value={field.columnIndex}>{field.displayName}</option>
                        ))}
                      </select>
                    </label>
                    <label className="data-source-field">
                      <span>Numeric value</span>
                      <select
                        value={chart.valueColumnIndex}
                        disabled={!canEditReport}
                        onChange={(event) => {
                          const nextValueColumnIndex = Number(event.target.value);
                          const nextCategory = selectedFields.find((field) => field.columnIndex !== nextValueColumnIndex);
                          updateChart(index, {
                            valueColumnIndex: nextValueColumnIndex,
                            ...(chart.categoryColumnIndex === nextValueColumnIndex && nextCategory
                              ? { categoryColumnIndex: nextCategory.columnIndex }
                              : {}),
                          });
                        }}
                      >
                        {numericFields.map((field) => (
                          <option key={field.columnIndex} value={field.columnIndex}>{field.displayName}</option>
                        ))}
                      </select>
                    </label>
                  </div>
                ))}
              </div>
              {selectedReport?.canPublish && (
                <div className="report-share-box">
                  <div className="report-field-editor-heading"><strong>Role access</strong><span>Published versions</span></div>
                  <label className="data-source-field">
                    <span>Organization role</span>
                    <select value={shareRoleId} onChange={(event) => setShareRoleId(event.target.value)}>
                      <option value="">Choose a role</option>
                      {roles.map((role) => <option key={role.roleId} value={role.roleId}>{role.name}</option>)}
                    </select>
                  </label>
                  <label className="report-share-checkbox"><input type="checkbox" checked={shareCanEdit} onChange={(event) => setShareCanEdit(event.target.checked)} /> Allow editing and new drafts</label>
                  <label className="report-share-checkbox"><input type="checkbox" checked={shareCanPublish} onChange={(event) => setShareCanPublish(event.target.checked)} /> Allow publishing and sharing</label>
                  <button className="button button--secondary" type="button" disabled={working || !shareRoleId} onClick={() => void shareWithRole()}>Save role access</button>
                  {shareNotice && <p className="report-share-notice" role="status">{shareNotice}</p>}
                </div>
              )}
            </aside>

            <div className="report-canvas">
              {!previewMode && <div className="report-canvas-ruler"><span>A4 REPORT PAGES</span><span>{(includeTable ? 1 : 0) + charts.length} COMPONENTS · {fields.length} FIELDS · {reportPages.length} PAGES</span></div>}
              <div className="report-pages">
                {reportPages.map((page, pageIndex) => (
                  <article className="report-paper" key={pageIndex}>
                    {effectivePrintHeader && <OrganizationReportHeaderPreview
                      className="report-print-header"
                      header={effectivePrintHeader}
                    />}
                    {pageIndex === 0 ? (
                      <header className="report-paper-header">
                        <span>ENTERPRISE REPORTING STUDIO</span>
                        <h1>{title || reportName || 'Untitled report'}</h1>
                        {description && <p>{description}</p>}
                        {category && <small>{category}</small>}
                      </header>
                    ) : (
                      <header className="report-running-header">
                        <span>ENTERPRISE REPORTING STUDIO</span>
                        <strong>{title || reportName || 'Untitled report'}</strong>
                      </header>
                    )}
                    {pageIndex === 0 && execution && (
                      <div className="report-results-meta">
                        <span>{execution.rows.length} rows · {execution.durationMilliseconds} ms</span>
                        {execution.truncated && <small>Results limited to first 100 rows</small>}
                      </div>
                    )}
                    {page.items.map((item) => {
                      if (item.type === 'table') {
                        return (
                          <section
                            className="report-page-table"
                            key={`table-${item.rowStart}`}
                            draggable={canEditReport && !previewMode}
                            onDragStart={(event) => {
                              event.dataTransfer.setData('text/plain', '-1');
                              event.dataTransfer.effectAllowed = 'move';
                            }}
                            onDragOver={(event) => event.preventDefault()}
                            onDrop={(event) => reorderComponent(event, -1)}
                          >
                            {item.rowStart === 0 && <h2>Details</h2>}
                            {execution ? (
                              <div className="report-results-table-wrap"><table className="report-results-table">
                                <thead><tr>{execution.fields.map((field, index) => <th key={`${index}-${field.columnName}`} style={{ width: field.width }}>{field.displayName}</th>)}</tr></thead>
                                <tbody>
                                  {execution.rows.slice(item.rowStart, item.rowEnd).map((row, rowIndex) => (
                                    <tr key={item.rowStart + rowIndex}>{row.map((value, index) => <td key={index}>{value === null ? <em>NULL</em> : String(value)}</td>)}</tr>
                                  ))}
                                  {execution.rows.length === 0 && <tr><td colSpan={execution.fields.length}>No rows were returned.</td></tr>}
                                </tbody>
                              </table></div>
                            ) : fields.length > 0 ? (
                              <table className="report-results-table">
                                <thead><tr>{fields.map((field) => <th key={field.columnIndex} style={{ width: field.width }}>{field.displayName}</th>)}</tr></thead>
                                <tbody><tr>{fields.map((field) => <td key={field.columnIndex}><span className="report-sample-cell">{field.columnName}</span></td>)}</tr></tbody>
                              </table>
                            ) : (
                              <div className="report-canvas-empty"><Icon name="file" size={22} /><strong>Your report canvas is ready</strong><span>Select dataset columns to define the report table.</span></div>
                            )}
                          </section>
                        );
                      }

                      const chart = (execution?.charts ?? charts)[item.chartIndex];
                      if (!chart) return null;
                      if (!execution) {
                        return (
                          <section
                            className="report-page-chart"
                            key={item.chartIndex}
                            draggable={canEditReport && !previewMode}
                            onDragStart={(event) => {
                              event.dataTransfer.setData('text/plain', String(item.chartIndex));
                              event.dataTransfer.effectAllowed = 'move';
                            }}
                            onDragOver={(event) => event.preventDefault()}
                            onDrop={(event) => reorderComponent(event, item.chartIndex)}
                          >
                            <h2>{chart.type} chart</h2>
                            <div className="report-canvas-empty"><strong>{chart.type} chart preview</strong><span>Preview the report to load chart data.</span></div>
                          </section>
                        );
                      }
                      const preview = getChartPreview(execution, chart);
                      return (
                        <section
                          className="report-page-chart"
                          key={item.chartIndex}
                          draggable={canEditReport && !previewMode}
                          onDragStart={(event) => {
                            event.dataTransfer.setData('text/plain', String(item.chartIndex));
                            event.dataTransfer.effectAllowed = 'move';
                          }}
                          onDragOver={(event) => event.preventDefault()}
                          onDrop={(event) => reorderComponent(event, item.chartIndex)}
                        >
                          {preview.data.length === 0 ? <p>The query returned no numeric values to chart.</p> : (
                            <Suspense fallback={<div className="report-chart-loading">Loading visualization…</div>}>
                              <ReportChartPreview
                                type={chart.type}
                                data={preview.data}
                                categoryName={preview.categoryName}
                                valueName={preview.valueName}
                              />
                            </Suspense>
                          )}
                        </section>
                      );
                    })}
                    <footer className="report-page-footer">
                      <span>{reportName || 'Enterprise Reporting Studio'}</span>
                      <span>Page {pageIndex + 1}</span>
                    </footer>
                  </article>
                ))}
              </div>
              {!previewMode && <div className="report-canvas-footnote"><Icon name="shield" size={15} /><span>Drag table and charts to reorder them, or use the Report components arrows. Preview is limited to 100 rows.</span></div>}
            </div>
          </div>
        </>
      )}
      {portableDialog && <div className="report-publish-backdrop" role="presentation" onMouseDown={(event) => {
        if (event.target === event.currentTarget) closePortableDialog();
      }}>
        <section className="report-publish-dialog portable-dialog" role="dialog" aria-modal="true" aria-labelledby="portable-dialog-title">
          <button className="report-publish-close" type="button" aria-label="Close" onClick={closePortableDialog} disabled={working}>
            <Icon name="x" size={18} />
          </button>
          <span className="panel-overline">PORTABLE REPORTS</span>
          <h2 id="portable-dialog-title">
            {portableDialog === 'export'
              ? portableContentType === 'dataset' ? 'Export a dataset' : 'Export and share a report'
              : 'Import a report'}
          </h2>
          <p>
            {portableDialog === 'export'
              ? portableContentType === 'dataset'
                ? 'Export a standalone dataset definition and optionally its data snapshot.'
                : 'Share the editable table-report definition, its dataset queries, and optionally a bounded data snapshot.'
              : 'Import a native .ersreport package or a supported Microsoft Report Builder .rdl file. Imported reports open in the designer as drafts.'}
          </p>
          {portableDialog === 'export' ? (
            <>
              <label className="portable-form-field">
                <span>Export type</span>
                <select value={portableContentType} onChange={(event) => {
                  setPortableContentType(event.target.value as 'report' | 'dataset');
                  setPortableLink(null);
                  setError(null);
                  if (event.target.value === 'dataset') setPortableFormat('ersreport');
                }}>
                  <option value="report">Table report and dependencies</option>
                  <option value="dataset" disabled={datasets.length === 0}>Dataset only</option>
                </select>
              </label>
              {portableContentType === 'report' ? (
                <>
                  <label className="portable-form-field">
                    <span>Report</span>
                    <select value={portableReportId} onChange={(event) => {
                      setPortableReportId(event.target.value);
                      setPortableLink(null);
                      setPortableLinkCopied(false);
                      setError(null);
                    }}>
                      {reports.map((report) => (
                        <option key={`${report.reportType ?? 'SingleSource'}:${report.reportId}`} value={`${report.reportType ?? 'SingleSource'}:${report.reportId}`}>
                          {report.name} · {report.isCurrentPublished ? 'Published' : 'Draft'}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="portable-form-field">
                    <span>File format</span>
                    <select value={portableFormat} onChange={(event) => {
                      setPortableFormat(event.target.value as 'ersreport' | 'rdl');
                      setError(null);
                    }}>
                      <option value="ersreport">Enterprise Reporting Studio package (.ersreport)</option>
                    <option value="rdl">Microsoft Report Definition (.rdl)</option>
                    </select>
                  </label>
                </>
              ) : (
                <label className="portable-form-field">
                  <span>Dataset</span>
                  <select value={portableDatasetId} onChange={(event) => {
                    setPortableDatasetId(event.target.value);
                    setError(null);
                  }}>
                    {datasets.map((dataset) => <option key={dataset.datasetId} value={dataset.datasetId}>{dataset.name}</option>)}
                  </select>
                </label>
              )}
              {portableFormat === 'ersreport' ? (
                <>
                  <label className="portable-check">
                    <input type="checkbox" checked={includeDatasetData} onChange={(event) => {
                      setIncludeDatasetData(event.target.checked);
                      setPortableLink(null);
                      setPortableLinkCopied(false);
                    }} />
                    <span>Include dataset data (up to 5,000 rows per dataset)</span>
                  </label>
                  <p className="portable-note">
                    {portableContentType === 'dataset'
                      ? 'The dataset schema is read from its source query. With data excluded, the package contains the definition and schema, not query result rows.'
                      : 'Schema is read from the source query during export. With data excluded, only dataset definitions and schema are packaged, not query result rows.'}
                  </p>
                </>
              ) : portableContentType === 'report' ? (
                <p className="portable-note">
                  {portableReportId.startsWith('MultiSource:')
                    ? 'Combined RDL exports include each dataset query, original server/database addresses, and the table layout, using Lookup expressions for related fields. Passwords are never exported. In Report Builder, use credentials that match each source authentication type and do not put credentials in the connection string, then click Run (F5). Join keys should be unique and matching across datasets.'
                    : 'RDL includes the query, original server/database address, and table layout, but never credentials. In Report Builder, use credentials that match the source authentication type and do not put credentials in the connection string, then click Run (F5).'}
                </p>
              ) : null}
              {portableFormat === 'ersreport' && portableContentType === 'report' && (
                <>
                  <label className="portable-check">
                    <input type="checkbox" checked={allowAnonymousLink} onChange={(event) => {
                      setAllowAnonymousLink(event.target.checked);
                      setPortableLink(null);
                    }} />
                    <span>Allow anyone with the link to download (otherwise require same-tenant sign-in)</span>
                  </label>
                  <p className="portable-note">Share links expire after 30 days and include the selected published version. A recipient must be able to reach this server URL.</p>
                </>
              )}
              {error && <p className="portable-error" role="alert">{error}</p>}
              {portableLink && (
                <label className="portable-form-field">
                  <span>Download link</span>
                  <input value={portableLink} readOnly onFocus={(event) => event.currentTarget.select()} />
                </label>
              )}
              <div className="report-publish-actions">
                <button className="button button--primary" type="button" onClick={() => void (portableContentType === 'dataset' ? exportPortableDataset() : exportPortableReport(portableFormat))} disabled={working || (portableContentType === 'dataset' ? !portableDatasetId : !portableReportId)}>
                  {working ? 'Preparing…' : portableContentType === 'dataset' ? 'Download dataset' : portableFormat === 'rdl' ? 'Download RDL' : 'Download package'}
                </button>
                {portableFormat === 'ersreport' && portableContentType === 'report' && (
                  <button className="button button--secondary" type="button" onClick={() => void createPortableLink()} disabled={working || !portableReportId || !reports.find((report) => `${report.reportType ?? 'SingleSource'}:${report.reportId}` === portableReportId)?.isCurrentPublished || !reports.find((report) => `${report.reportType ?? 'SingleSource'}:${report.reportId}` === portableReportId)?.canPublish}>
                    Create share link
                  </button>
                )}
                {portableLink && <button className="button button--secondary" type="button" onClick={() => void copyPortableLink()}>{portableLinkCopied ? 'Copied' : 'Copy link'}</button>}
                <button className="button button--secondary" type="button" onClick={closePortableDialog} disabled={working}>Done</button>
              </div>
            </>
          ) : (
            <>
              <label className="portable-form-field">
                <span>Report package</span>
                <input type="file" accept=".ersreport,.ersdataset,.rdl,application/xml,application/json" onChange={(event) => void selectPortableFile(event)} disabled={working} />
              </label>
              {portablePackage && (
                <>
                  <p className="portable-package-name"><strong>{portablePackage.name}</strong> · {portableDatasetImport ? 'Standalone dataset' : portablePackage.reportType === 'MultiSource' ? 'Combined table report' : 'Table report'}</p>
                  {portablePackage.datasets.map((dataset) => (
                    <label className="portable-form-field" key={dataset.originalDatasetId}>
                      <span>{dataset.alias}: {dataset.name}</span>
                      <select value={datasetMappings[dataset.originalDatasetId] ?? ''} onChange={(event) => setDatasetMappings(current => ({
                        ...current,
                        [dataset.originalDatasetId]: event.target.value,
                      }))}>
                        <option value="">Choose destination data source</option>
                        {sources.map((source) => <option key={source.dataSourceId} value={source.dataSourceId}>{source.name} · {source.databaseName}</option>)}
                      </select>
                    </label>
                  ))}
                  {portablePackage.limitations.map((limitation) => <p className="portable-note" key={limitation}>{limitation}</p>)}
                </>
              )}
              {error && <p className="portable-error" role="alert">{error}</p>}
              <div className="report-publish-actions">
                <button className="button button--primary" type="button" onClick={() => void importPortableReport()} disabled={working || !portablePackage || sources.length === 0}>
                  {working ? 'Importing…' : portableDatasetImport ? 'Import dataset' : 'Import and open in designer'}
                </button>
                <button className="button button--secondary" type="button" onClick={closePortableDialog} disabled={working}>Cancel</button>
              </div>
            </>
          )}
        </section>
      </div>}
      {publishDialogOpen && selectedReport && (
        <div className="report-publish-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !working) setPublishDialogOpen(false);
        }}>
          <section className="report-publish-dialog report-category-dialog" role="dialog" aria-modal="true" aria-labelledby="report-category-title">
            <button className="report-publish-close" type="button" aria-label="Close publish options" onClick={() => setPublishDialogOpen(false)} disabled={working}>
              <Icon name="x" size={18} />
            </button>
            <span className="panel-overline">PUBLISH REPORT</span>
            <h2 id="report-category-title">Choose a user dashboard folder</h2>
            <p>{selectedReport.name} will appear in the selected section after publishing.</p>
            <ReportFolderPicker
              folders={reportFolders}
              value={publishCategory}
              onChange={setPublishCategory}
              onCreate={createReportFolder}
              onDelete={deleteReportFolder}
              disabled={working || publishFoldersLoading}
            />
            {publishFoldersLoading && <p role="status">Loading dashboard folders…</p>}
            {error && <button className="button button--secondary" type="button" onClick={() => void openPublishDialog()} disabled={publishFoldersLoading}>Retry loading folders</button>}
            {error && <p className="report-publish-link-error" role="alert">{error}</p>}
            <div className="report-publish-actions">
              <button className="button button--primary" type="button" onClick={() => void publishDraft()} disabled={working || publishFoldersLoading || !publishCategory}>
                {working ? <span className="spinner spinner--small" /> : <Icon name="check" size={15} />} Publish to {publishCategory}
              </button>
              <button className="button button--secondary" type="button" onClick={() => setPublishDialogOpen(false)} disabled={working}>Cancel</button>
            </div>
          </section>
        </div>
      )}
      {publishedConfirmation && (
        <div className="report-publish-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget) setPublishedConfirmation(null);
        }}>
          <section className="report-publish-dialog" role="dialog" aria-modal="true" aria-labelledby="report-publish-title">
            <button className="report-publish-close" type="button" aria-label="Close publish confirmation" onClick={() => setPublishedConfirmation(null)}>
              <Icon name="x" size={18} />
            </button>
            <span className="report-publish-success-icon"><Icon name="check" size={20} /></span>
            <span className="panel-overline">PUBLISH COMPLETE</span>
            <h2 id="report-publish-title">{publishedConfirmation.name} is published</h2>
            <p>This permanent link downloads the current published version as a PDF. Anyone with the link can access it.</p>
            {publishedConfirmation.loading ? (
              <div className="report-publish-link-loading"><span className="spinner spinner--small" /> Preparing your share link…</div>
            ) : publishedConfirmation.url ? (
              <label className="report-publish-link">
                <span>Published report URL</span>
                <input value={publishedConfirmation.url} readOnly onFocus={(event) => event.currentTarget.select()} aria-label="Published report URL" />
              </label>
            ) : null}
            {publishedConfirmation.error && <p className="report-publish-link-error" role="alert">{publishedConfirmation.error}</p>}
            <div className="report-publish-actions">
              <button className="button button--primary" type="button" onClick={() => void copyPublishedUrl()} disabled={!publishedConfirmation.url}>
                <Icon name="file" size={15} />{publishUrlCopied ? 'URL copied' : 'Copy URL'}
              </button>
              <button className="button button--secondary" type="button" onClick={() => setPublishedConfirmation(null)}>Done</button>
            </div>
          </section>
        </div>
      )}
    </section>
  );
}
