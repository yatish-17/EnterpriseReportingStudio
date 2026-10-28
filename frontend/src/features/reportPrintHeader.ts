export interface ReportPrintHeader {
  title: string;
  subtitle: string;
  logoDataUrl: string | null;
  alignment: 'left' | 'center' | 'right';
  elements: ReportPrintHeaderElement[];
  template?: string;
}

export type ReportPrintHeaderElementType = 'title' | 'subtitle' | 'logo';

export interface ReportPrintHeaderElement {
  type: ReportPrintHeaderElementType;
  x: number;
  y: number;
  size: number;
}

export const organizationHeaderTemplate = 'organization-header-v1';

export function isOrganizationHeaderTemplate(template?: string | null): boolean {
  return template === organizationHeaderTemplate ||
    (typeof template === 'string' &&
      (template.endsWith('-peach-v1') || template.endsWith('-report-v2')));
}

function defaultElementPosition(
  type: ReportPrintHeaderElementType,
  alignment: ReportPrintHeader['alignment'],
  hasLogo: boolean,
): ReportPrintHeaderElement {
  const textX = alignment === 'center'
    ? 50
    : alignment === 'right'
      ? hasLogo ? 78 : 90
      : hasLogo ? 40 : 10;
  if (type === 'logo') {
    return { type, x: alignment === 'right' ? 94 : alignment === 'center' ? 50 : 7, y: 40, size: 30 };
  }
  return {
    type,
    x: textX,
    y: type === 'title' ? 24 : 65,
    size: type === 'title' ? 18 : 12,
  };
}

function elementSize(type: ReportPrintHeaderElementType, size: unknown) {
  const bounds = type === 'title'
    ? [8, 36, 18]
    : type === 'subtitle'
      ? [7, 24, 12]
      : [12, 60, 30];
  return typeof size === 'number' && Number.isFinite(size)
    ? Math.max(bounds[0], Math.min(bounds[1], size))
    : bounds[2];
}

export function reportPrintHeaderElements(header: Partial<ReportPrintHeader>) {
  const alignment = header.alignment === 'center' || header.alignment === 'right'
    ? header.alignment
    : 'left';
  const activeTypes: ReportPrintHeaderElementType[] = [
    ...(header.title?.trim() ? ['title' as const] : []),
    ...(header.subtitle?.trim() ? ['subtitle' as const] : []),
    ...(header.logoDataUrl ? ['logo' as const] : []),
  ];
  const savedElements = Array.isArray(header.elements) ? header.elements : [];
  return activeTypes.map((type) => {
    const saved = savedElements.find((element) => element?.type === type);
    return saved && Number.isFinite(saved.x) && Number.isFinite(saved.y)
      ? {
          type,
          x: Math.max(0, Math.min(100, saved.x)),
          y: Math.max(0, Math.min(100, saved.y)),
          size: elementSize(type, saved.size),
        }
      : defaultElementPosition(type, alignment, Boolean(header.logoDataUrl));
  });
}

export function normalizePrintHeader(header?: Partial<ReportPrintHeader> | null): ReportPrintHeader | null {
  if (!header) return null;
  const normalized: ReportPrintHeader = {
    title: typeof header.title === 'string' ? header.title.trim() : '',
    subtitle: typeof header.subtitle === 'string' ? header.subtitle.trim() : '',
    logoDataUrl: typeof header.logoDataUrl === 'string' ? header.logoDataUrl : null,
    alignment: header.alignment === 'center' || header.alignment === 'right' ? header.alignment : 'left',
    elements: [],
    template: isOrganizationHeaderTemplate(header.template)
      ? organizationHeaderTemplate
      : undefined,
  };
  normalized.elements = reportPrintHeaderElements({ ...normalized, elements: header.elements });
  return normalized.title || normalized.subtitle || normalized.logoDataUrl ? normalized : null;
}
