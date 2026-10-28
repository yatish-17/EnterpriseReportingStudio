import {
  isOrganizationHeaderTemplate,
  organizationHeaderTemplate,
  normalizePrintHeader,
  reportPrintHeaderElements,
  type ReportPrintHeader,
  type ReportPrintHeaderElement,
} from './reportPrintHeader';

export { isOrganizationHeaderTemplate, organizationHeaderTemplate };
export const organizationHeaderArtwork = '/report-header-neutral.svg';
export const organizationHeaderAspectRatio = 2164 / 306;
export const organizationHeaderMaximumWidth = 297;

export function organizationHeaderDimensions(pageWidth: number) {
  const width = Math.min(pageWidth, organizationHeaderMaximumWidth);
  return { width, height: width / organizationHeaderAspectRatio };
}

export async function createOrganizationHeaderImage(title: string) {
  const response = await fetch(organizationHeaderArtwork);
  if (!response.ok) {
    throw new Error('The fixed report header artwork could not be loaded.');
  }

  const bitmap = await createImageBitmap(await response.blob());
  const canvas = document.createElement('canvas');
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  const context = canvas.getContext('2d');
  if (!context) {
    bitmap.close();
    throw new Error('The report header image could not be prepared.');
  }

  context.drawImage(bitmap, 0, 0);
  bitmap.close();
  context.strokeStyle = '#c89c3a';
  context.lineWidth = Math.max(2, canvas.width * 0.0012);
  context.beginPath();
  context.moveTo(canvas.width * 0.82, canvas.height * 0.28);
  context.lineTo(canvas.width * 0.82, canvas.height * 0.72);
  context.stroke();

  const maxTextWidth = canvas.width * 0.68;
  let fontSize = canvas.width * 0.03;
  context.textBaseline = 'middle';
  context.fillStyle = '#17464b';
  context.font = `bold ${fontSize}px "Times New Roman", Georgia, serif`;
  while (fontSize > 12 && context.measureText(title).width > maxTextWidth) {
    fontSize -= 1;
    context.font = `bold ${fontSize}px "Times New Roman", Georgia, serif`;
  }

  context.fillText(
    title,
    canvas.width * 0.07,
    canvas.height * 0.5,
    maxTextWidth,
  );
  return canvas.toDataURL('image/jpeg', 0.95);
}

export function createOrganizationReportHeader(
  title: string,
  logoDataUrl: string | null,
  elements?: ReportPrintHeaderElement[],
): ReportPrintHeader {
  const header: ReportPrintHeader = {
    title,
    subtitle: '',
    logoDataUrl,
    alignment: 'left',
    elements: elements ?? [],
  };
  header.elements = reportPrintHeaderElements(header);
  return header;
}

export async function resolveOrganizationReportHeader(
  header?: Partial<ReportPrintHeader> | null,
): Promise<ReportPrintHeader | null> {
  if (!header) {
    return null;
  }

  if (!isOrganizationHeaderTemplate(header.template)) {
    return normalizePrintHeader(header);
  }
  if (!header.title?.trim()) return null;

  const title = header.title.trim();
  return {
    title,
    subtitle: header.subtitle ?? '',
    logoDataUrl: await createOrganizationHeaderImage(title),
    alignment: 'left',
    elements: [],
    template: organizationHeaderTemplate,
  };
}
