import type { jsPDF as JsPDFDocument } from 'jspdf';
import type autoTableType from 'jspdf-autotable';
import {
  isOrganizationHeaderTemplate,
  reportPrintHeaderElements,
  type ReportPrintHeader,
} from './reportPrintHeader';
import {
  organizationHeaderAspectRatio,
  organizationHeaderDimensions,
  organizationHeaderTemplate,
} from './organizationReportHeader';

export interface ReportDownloadComponent {
  title: string;
  type: 'Table' | 'Bar' | 'Line' | 'Pie';
  columns: readonly string[];
  rows: readonly (readonly unknown[])[];
}

export interface ReportPdfMetadata {
  parameters?: readonly { name: string; value: unknown }[];
  publishedBy?: string | null;
  publishedAt?: string | null;
  versionNumber?: number | null;
}

export function reportPdfPageWidth(components: readonly ReportDownloadComponent[]) {
  const widestColumnCount = Math.max(1, ...components.map((component) => component.columns.length));
  return widestColumnCount > 5 ? 297 : 210;
}

const reportColors = {
  navy: [12, 48, 92] as [number, number, number],
  blue: [35, 114, 232] as [number, number, number],
  green: [83, 177, 79] as [number, number, number],
  orange: [250, 151, 24] as [number, number, number],
  ink: [35, 53, 79] as [number, number, number],
  muted: [101, 119, 145] as [number, number, number],
  grid: [218, 226, 236] as [number, number, number],
  paleBlue: [239, 245, 252] as [number, number, number],
};

const chartPalette: readonly [number, number, number][] = [
  [78, 139, 114],
  [102, 137, 160],
  [192, 150, 88],
  [137, 119, 169],
  [94, 154, 154],
  [185, 114, 104],
];

interface PagedTableOptions {
  title: string;
  columns: readonly string[];
  rows: readonly (readonly unknown[])[];
  startY: number;
  pageTopMargin: number;
  sectionNumber?: number;
  startOnNewPage?: boolean;
}

function drawSectionLabel(doc: JsPDFDocument, sectionNumber: number, type: 'TABLE' | 'CHART', x: number, y: number) {
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(7.5);
  doc.setTextColor(...reportColors.blue);
  doc.text(`${String(sectionNumber).padStart(2, '0')} · ${type}`, x, y);
}

export function addPagedReportTable(
  autoTable: typeof autoTableType,
  doc: JsPDFDocument,
  {
    title,
    columns,
    rows,
    startY,
    pageTopMargin,
    sectionNumber = 1,
    startOnNewPage = false,
  }: PagedTableOptions,
) {
  const margin = 12;
  const pageHeight = doc.internal.pageSize.getHeight();
  const availableWidth = doc.internal.pageSize.getWidth() - margin * 2;
  if (startOnNewPage || startY > pageHeight - 42) {
    doc.addPage();
    startY = pageTopMargin;
  }

  doc.setFont('helvetica', 'normal');
  doc.setFontSize(8.5);
  const preferredColumnWidths = columns.map((column, columnIndex) => {
    doc.setFont('helvetica', 'bold');
    const headerWordWidth = column.split(/\s+/).reduce(
      (widest, word) => Math.max(widest, doc.getTextWidth(word)),
      0,
    );
    doc.setFont('helvetica', 'normal');
    const valueWidth = rows.reduce((widest, row) => {
      const value = row[columnIndex] == null ? '' : String(row[columnIndex]);
      return Math.max(widest, doc.getTextWidth(value));
    }, 0);
    return Math.max(8, headerWordWidth, valueWidth) + 5;
  });
  const preferredWidth = Math.max(1, preferredColumnWidths.reduce((sum, width) => sum + width, 0));
  const fontScale = Math.min(1, availableWidth / preferredWidth);
  const fontSize = 8.5 * fontScale;
  const cellPadding = fontSize * 0.28;
  const columnStyles = Object.fromEntries(preferredColumnWidths.map((width, index) => [
    index,
    { cellWidth: availableWidth * width / preferredWidth },
  ]));

  drawSectionLabel(doc, sectionNumber, 'TABLE', margin, startY);
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(11);
  doc.setTextColor(...reportColors.navy);
  doc.text(title, margin, startY + 6);

  autoTable(doc, {
    head: [Array.from(columns)],
    body: rows.map((row) => row.map((value) => value == null ? '' : String(value))),
    startY: startY + 10,
    margin: { left: margin, right: margin, top: pageTopMargin, bottom: 20 },
    tableWidth: 'auto',
    columnStyles,
    styles: {
      font: 'helvetica',
      fontSize,
      cellPadding,
      minCellWidth: 0,
      overflow: 'linebreak',
      valign: 'middle',
      lineColor: reportColors.grid,
      lineWidth: 0.2,
      textColor: reportColors.ink,
    },
    headStyles: {
      fillColor: reportColors.paleBlue,
      textColor: reportColors.navy,
      fontStyle: 'bold',
      fontSize,
      lineColor: [203, 216, 232],
    },
    alternateRowStyles: { fillColor: [248, 250, 253] },
  });

  const finalY = (doc as JsPDFDocument & { lastAutoTable?: { finalY: number } }).lastAutoTable?.finalY;
  return (finalY ?? startY + 30) + 14;
}

function safeFileName(value: string) {
  return value.replace(/[<>:"/\\|?*]/g, '_').trim() || 'report';
}

function escapeHtml(value: unknown) {
  return String(value ?? '').replace(/[&<>"']/g, (character) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;',
  })[character] ?? character);
}

function spreadsheetValue(value: unknown) {
  const text = String(value ?? '');
  return typeof value === 'string' && /^[\s]*[=+\-@]/.test(text) ? `'${text}` : text;
}

export function excelHeaderMarkup(header?: ReportPrintHeader | null) {
  if (!header) return '';
  if (header.template === organizationHeaderTemplate && header.logoDataUrl) {
    return `<div class="report-brand report-brand--template"><img src="${escapeHtml(header.logoDataUrl)}" alt="${escapeHtml(header.title)}"></div>`;
  }
  const items = reportPrintHeaderElements(header).map((element) => {
    const alignment = element.x < 24 ? 'left' : element.x > 76 ? 'right' : 'center';
    const x = 14 + (210 - 28) * element.x / 100;
    const y = 4 + element.y * 0.22;
    const transform = alignment === 'left'
      ? 'translateY(-50%)'
      : alignment === 'right' ? 'translate(-100%,-50%)' : 'translate(-50%,-50%)';
    const left = x / 210 * 100;
    const top = y / (210 / organizationHeaderAspectRatio) * 100;
    const position = `left:${left}%;top:${top}%;transform:${transform};font-size:${element.size}pt`;
    if (element.type === 'logo') {
      return `<img class="report-brand-item report-brand-logo" src="${escapeHtml(header.logoDataUrl ?? '')}" alt="" style="${position};width:${element.size}mm;height:${Math.min(element.size * 0.6, 27)}mm">`;
    }
    const textWidth = alignment === 'left'
      ? 210 - 14 - x
      : alignment === 'right'
        ? x - 14
        : Math.min(x - 14, 210 - 14 - x) * 2;
    const boundedPosition = `${position};width:${textWidth / 210 * 100}%`;
    const text = element.type === 'title'
      ? `<strong>${escapeHtml(header.title)}</strong>`
      : `<small>${escapeHtml(header.subtitle)}</small>`;
    return `<span class="report-brand-item report-brand-text report-brand-text--${alignment}" style="${boundedPosition}">${text}</span>`;
  });
  return `<div class="report-brand">${items.join('')}</div>`;
}

export function downloadExcelReport(
  fileName: string,
  components: readonly ReportDownloadComponent[],
  header?: ReportPrintHeader | null,
) {
  const tables = components.map((component) =>
    `${excelHeaderMarkup(header)}<h2>${escapeHtml(component.title)}</h2><table><thead><tr>${component.columns.map((column) => `<th>${escapeHtml(column)}</th>`).join('')}</tr></thead><tbody>${component.rows.map((row) => `<tr>${row.map((value) => `<td>${escapeHtml(spreadsheetValue(value))}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
  const html = `<html><head><meta charset="utf-8"><style>.report-brand{position:relative;height:140px;overflow:hidden;margin:0 0 12px;border-bottom:1px solid #dce5de;font-family:Arial,sans-serif;box-sizing:border-box}.report-brand--template{height:auto;margin:0 0 12px;border:0}.report-brand--template img{display:block;width:100%;height:auto}.report-brand-item{position:absolute;max-width:100%;box-sizing:border-box}.report-brand-logo{object-fit:contain}.report-brand-text{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.report-brand-text--left{text-align:left}.report-brand-text--center{text-align:center}.report-brand-text--right{text-align:right}.report-brand strong{color:#255b49;font-size:inherit}.report-brand small{display:block;color:#64766a;font-size:inherit}table{border-collapse:collapse;margin-bottom:24px}th,td{border:1px solid #cbd8ce;padding:5px;text-align:left}th{background:#255b49;color:#fff}</style></head><body>${tables.join('')}</body></html>`;
  const blob = new Blob([html], { type: 'application/vnd.ms-excel;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = `${safeFileName(fileName)}.xls`;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function drawReportPrintHeader(
  doc: JsPDFDocument,
  header: ReportPrintHeader,
  pageWidth = doc.internal.pageSize.getWidth(),
) {
  if (isOrganizationHeaderTemplate(header.template) &&
      header.logoDataUrl) {
    const imageType = header.logoDataUrl.startsWith('data:image/png;') ? 'PNG' : 'JPEG';
    const { width, height } = organizationHeaderDimensions(pageWidth);
    doc.addImage(header.logoDataUrl, imageType, (pageWidth - width) / 2, 0, width, height);
    return;
  }

  const margin = 14;
  const contentWidth = pageWidth - margin * 2;
  for (const element of reportPrintHeaderElements(header)) {
    const x = margin + contentWidth * element.x / 100;
    const requestedY = 4 + element.y * 0.22;
    if (element.type === 'logo' && header.logoDataUrl) {
      const imageType = header.logoDataUrl.startsWith('data:image/png;') ? 'PNG' : 'JPEG';
      const properties = doc.getImageProperties(header.logoDataUrl);
      const aspectRatio = properties.width / properties.height;
      const defaultHeight = Math.min(18, 30 / aspectRatio);
      const height = Math.min(27, defaultHeight * element.size / 30);
      const width = height * aspectRatio;
      const alignment = element.x < 24 ? 'left' : element.x > 76 ? 'right' : 'center';
      const minimumX = alignment === 'left'
        ? margin
        : alignment === 'right' ? margin + width : margin + width / 2;
      const maximumX = alignment === 'left'
        ? pageWidth - margin - width
        : alignment === 'right' ? pageWidth - margin : pageWidth - margin - width / 2;
      const boundedX = Math.max(minimumX, Math.min(maximumX, x));
      const imageX = alignment === 'left'
        ? boundedX
        : alignment === 'right' ? boundedX - width : boundedX - width / 2;
      const centerY = Math.max(
        height / 2 + 1,
        Math.min(31 - height / 2, requestedY),
      );
      doc.addImage(header.logoDataUrl, imageType, imageX, centerY - height / 2, width, height);
      continue;
    }

    const value = element.type === 'title' ? header.title : header.subtitle;
    if (!value) continue;
    const alignment = element.x < 24 ? 'left' : element.x > 76 ? 'right' : 'center';
    const textWidth = alignment === 'left'
      ? pageWidth - margin - x
      : alignment === 'right' ? x - margin : Math.min(x - margin, pageWidth - margin - x) * 2;
    const minimumSize = element.type === 'title' ? 8 : 7;
    let fontSize = element.size;
    doc.setFont('helvetica', element.type === 'title' ? 'bold' : 'normal');
    doc.setFontSize(fontSize);
    while (fontSize > minimumSize && doc.getTextWidth(value) > textWidth) {
      fontSize = Math.max(minimumSize, fontSize - 0.5);
      doc.setFontSize(fontSize);
    }
    let visibleValue = value;
    if (doc.getTextWidth(visibleValue) > textWidth) {
      while (visibleValue.length > 1 && doc.getTextWidth(`${visibleValue}...`) > textWidth) {
        visibleValue = visibleValue.slice(0, -1);
      }
      visibleValue = visibleValue.length < value.length ? `${visibleValue}...` : visibleValue;
    }
    const textHalfHeight = fontSize * 25.4 / 72 / 2;
    const y = Math.max(
      textHalfHeight + 1,
      Math.min(31 - textHalfHeight, requestedY),
    );
    if (element.type === 'title') {
      doc.setTextColor(39, 71, 55);
    } else {
      doc.setTextColor(103, 117, 109);
    }
    doc.text(visibleValue, x, y, { align: alignment, baseline: 'middle' });
  }
  doc.setDrawColor(210, 222, 213);
  doc.setLineWidth(0.35);
  doc.line(margin, 32, pageWidth - margin, 32);
}

function drawChart(
  doc: JsPDFDocument,
  component: ReportDownloadComponent,
  top: number,
  pageTopMargin: number,
  sectionNumber: number,
) {
  const pageWidth = doc.internal.pageSize.getWidth();
  const pageHeight = doc.internal.pageSize.getHeight();
  const margin = 12;
  const labels = component.rows.map((row) => String(row[0] ?? '(blank)')).slice(0, 12);
  const chartSeries = component.columns.slice(1).map((name, seriesIndex) => ({
    name,
    values: component.rows.slice(0, 12).map((row) => {
      const value = row[seriesIndex + 1] == null ? Number.NaN : Number(row[seriesIndex + 1]);
      return Number.isFinite(value) ? value : null;
    }),
  })).filter((series) => series.values.some((value) => value !== null));
  if (!chartSeries.length && component.rows.some((row) => row.length > 1)) {
    const values = component.rows.slice(0, 12).map((row) => {
      const value = row[1] == null ? Number.NaN : Number(row[1]);
      return Number.isFinite(value) ? value : null;
    });
    if (values.some((value) => value !== null)) {
      chartSeries.push({ name: component.columns[1] ?? component.title, values });
    }
  }
  const points = labels.map((label, index) => ({
    label,
    values: chartSeries.map((series) => series.values[index] ?? 0),
  }));
  const showSeriesLegend = chartSeries.length > 1;
  const height = component.type === 'Pie'
    ? Math.max(94, 35 + points.length * 7)
    : showSeriesLegend
      ? Math.max(104, 96 + Math.ceil(chartSeries.length / 2) * 7)
      : 90;
  if (top + height > pageHeight - 20) {
    doc.addPage();
    top = pageTopMargin;
  }
  drawSectionLabel(doc, sectionNumber, 'CHART', margin, top);
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(11);
  doc.setTextColor(...reportColors.navy);
  doc.text(component.title, margin, top + 6, { maxWidth: pageWidth - margin * 2 });
  if (!points.length || !chartSeries.length) {
    doc.setFont('helvetica', 'normal');
    doc.setFontSize(9);
    doc.setTextColor(...reportColors.muted);
    doc.text('No numeric values are available for this chart.', margin, top + 22);
    return top + 38;
  }

  if (component.type === 'Pie') {
    const piePoints = points.map((point) => ({ label: point.label, value: point.values[0] ?? 0 }))
      .filter((point) => point.value > 0);
    const total = piePoints.reduce((sum, point) => sum + point.value, 0);
    if (total <= 0) return top + height;
    const centerX = margin + 41;
    const centerY = top + 52;
    const radius = 27;
    let angle = -Math.PI / 2;
    piePoints.forEach((point, index) => {
      const sweep = point.value / total * Math.PI * 2;
      const pieces = Math.max(1, Math.ceil(sweep / (Math.PI / 36)));
      const color = chartPalette[index % chartPalette.length];
      doc.setFillColor(...color);
      for (let piece = 0; piece < pieces; piece++) {
        const startAngle = angle + sweep * piece / pieces;
        const endAngle = angle + sweep * (piece + 1) / pieces;
        doc.triangle(
          centerX,
          centerY,
          centerX + Math.cos(startAngle) * radius,
          centerY + Math.sin(startAngle) * radius,
          centerX + Math.cos(endAngle) * radius,
          centerY + Math.sin(endAngle) * radius,
          'F',
        );
      }
      angle += sweep;
    });
    doc.setFillColor(255, 255, 255);
    doc.circle(centerX, centerY, 13, 'F');
    doc.setFont('helvetica', 'bold');
    doc.setFontSize(8);
    doc.setTextColor(...reportColors.navy);
    doc.text(total.toLocaleString(), centerX, centerY + 1, { align: 'center' });

    const legendX = margin + 88;
    const legendY = top + 23;
    const legendWidth = pageWidth - legendX - margin;
    piePoints.forEach((point, index) => {
      const y = legendY + index * 7;
      const color = chartPalette[index % chartPalette.length];
      doc.setFillColor(...color);
      doc.circle(legendX, y - 1, 1.5, 'F');
      doc.setFont('helvetica', 'normal');
      doc.setFontSize(8);
      doc.setTextColor(...reportColors.ink);
      doc.text(`${point.label}  ·  ${((point.value / total) * 100).toFixed(1)}%`, legendX + 5, y, {
        maxWidth: legendWidth - 5,
      });
    });
    return top + height;
  }

  const chartTop = top + 22;
  const chartBottom = top + 75;
  const chartLeft = margin + 25;
  const chartRight = pageWidth - margin - 4;
  const chartWidth = Math.max(30, chartRight - chartLeft);
  doc.setFillColor(251, 252, 254);
  doc.setDrawColor(229, 234, 241);
  doc.setLineWidth(0.25);
  doc.roundedRect(
    margin,
    top + 10,
    pageWidth - margin * 2,
    height - 12,
    2,
    2,
    'FD',
  );
  const values = points.flatMap((point) => point.values);
  const minimum = Math.min(0, ...values);
  const maximum = Math.max(0, ...values);
  const range = maximum - minimum || 1;
  const zeroY = chartBottom - ((0 - minimum) / range) * (chartBottom - chartTop);
  doc.setFont('helvetica', 'normal');
  doc.setFontSize(7);
  doc.setTextColor(...reportColors.muted);
  for (let tick = 0; tick <= 4; tick++) {
    const value = minimum + range * tick / 4;
    const y = chartBottom - (value - minimum) / range * (chartBottom - chartTop);
    doc.setDrawColor(...reportColors.grid);
    doc.setLineWidth(0.2);
    doc.line(chartLeft, y, chartRight, y);
    doc.text(value.toLocaleString(undefined, { maximumFractionDigits: 1 }), chartLeft - 3, y + 1, { align: 'right' });
  }

  const slotWidth = chartWidth / points.length;
  const plottedPoints = points.map((point, index) => ({
    ...point,
    x: chartLeft + slotWidth * (index + 0.5),
    values: point.values.map((value) => ({
      value,
      y: chartBottom - (value - minimum) / range * (chartBottom - chartTop),
    })),
  }));
  if (component.type === 'Line') {
    chartSeries.forEach((_, seriesIndex) => {
      const color = chartPalette[seriesIndex % chartPalette.length];
      doc.setDrawColor(...color);
      doc.setLineWidth(1.15);
      for (let index = 1; index < plottedPoints.length; index++) {
        const previous = plottedPoints[index - 1];
        const current = plottedPoints[index];
        doc.line(previous.x, previous.values[seriesIndex].y, current.x, current.values[seriesIndex].y);
      }
      plottedPoints.forEach((point) => {
        doc.setFillColor(...color);
        doc.circle(point.x, point.values[seriesIndex].y, 1.6, 'F');
      });
    });
  } else {
    const barWidth = Math.min(15 / chartSeries.length, slotWidth * 0.72 / chartSeries.length);
    plottedPoints.forEach((point, pointIndex) => {
      point.values.forEach((item, seriesIndex) => {
        const color = chartPalette[(chartSeries.length === 1 ? pointIndex : seriesIndex) % chartPalette.length];
        const x = point.x + (seriesIndex - (chartSeries.length - 1) / 2) * barWidth;
        doc.setFillColor(...color);
        doc.setDrawColor(251, 252, 254);
        doc.setLineWidth(0.35);
        doc.roundedRect(
          x - barWidth / 2,
          Math.min(item.y, zeroY),
          barWidth,
          Math.max(0.8, Math.abs(zeroY - item.y)),
          0.8,
          0.8,
          'F',
        );
      });
    });
  }

  doc.setFont('helvetica', 'normal');
  doc.setFontSize(7.5);
  doc.setTextColor(...reportColors.muted);
  plottedPoints.forEach((point) => {
    const lines = doc.splitTextToSize(point.label, Math.max(8, slotWidth - 2)).slice(0, 2);
    doc.text(lines, point.x, chartBottom + 5, { align: 'center', maxWidth: slotWidth });
  });
  if (showSeriesLegend) {
    const legendY = top + 96;
    const legendColumnWidth = chartWidth / 2;
    chartSeries.forEach((series, seriesIndex) => {
      const color = chartPalette[seriesIndex % chartPalette.length];
      const legendColumn = seriesIndex % 2;
      const legendRow = Math.floor(seriesIndex / 2);
      const legendX = chartLeft + legendColumn * legendColumnWidth;
      const y = legendY + legendRow * 7;
      doc.setFillColor(...color);
      doc.circle(legendX + 1, y - 1, 1.6, 'F');
      doc.setFontSize(7.5);
      doc.setTextColor(...reportColors.ink);
      doc.text(series.name, legendX + 5, y, { maxWidth: legendColumnWidth - 8 });
    });
  }
  return top + height;
}

function reportPageHeight(pageWidth: number) {
  return pageWidth === 210 ? 297 : 210;
}

function reportCategory(subtitle: string) {
  const [category, ...detail] = subtitle.split(' · ');
  return { category: category?.trim() || 'REPORT', detail: detail.join(' · ').trim() };
}

function drawReportTitle(
  doc: JsPDFDocument,
  title: string,
  subtitle: string,
  startY: number,
  pageWidth: number,
  metadata?: ReportPdfMetadata,
) {
  const margin = 12;
  const { category, detail } = reportCategory(subtitle);
  const metadataLines = [
    ...(metadata?.parameters ?? []).map(({ name, value }) =>
      `${name}: ${value == null || value === '' ? '—' : String(value)}`),
    ...(metadata?.publishedBy ? [`Published by: ${metadata.publishedBy}`] : []),
    ...(metadata?.publishedAt ? [`Published: ${new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(metadata.publishedAt))}`] : []),
    ...(metadata?.versionNumber != null ? [`Version: ${metadata.versionNumber}`] : []),
  ];
  const metadataWidth = metadataLines.length > 0 ? Math.min(78, pageWidth * 0.34) : 0;
  const leftWidth = pageWidth - margin * 2 - metadataWidth - (metadataWidth ? 8 : 0);
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(7.5);
  doc.setTextColor(...reportColors.muted);
  doc.text(category.toUpperCase(), margin, startY);

  doc.setFont('times', 'bold');
  doc.setFontSize(20);
  doc.setTextColor(...reportColors.navy);
  const titleLines = doc.splitTextToSize(title, leftWidth);
  doc.text(titleLines, margin, startY + 11);
  let cursor = startY + 11 + titleLines.length * 8;

  if (detail) {
    doc.setFont('helvetica', 'normal');
    doc.setFontSize(8.5);
    doc.setTextColor(...reportColors.muted);
    const detailLines = doc.splitTextToSize(detail, leftWidth);
    doc.text(detailLines, margin, cursor);
    cursor += detailLines.length * 4.5;
  }

  let metadataCursor = startY + 2;
  if (metadataLines.length > 0) {
    doc.setFont('helvetica', 'normal');
    doc.setFontSize(7.5);
    doc.setTextColor(...reportColors.muted);
    for (const line of metadataLines) {
      const wrapped = doc.splitTextToSize(line, metadataWidth);
      doc.text(wrapped, pageWidth - margin, metadataCursor, { align: 'right' });
      metadataCursor += wrapped.length * 3.7;
    }
  }

  cursor = Math.max(cursor, metadataCursor);
  cursor += 4;
  doc.setDrawColor(192, 207, 226);
  doc.setLineWidth(0.35);
  doc.line(margin, cursor, pageWidth - margin, cursor);
  return cursor + 10;
}

export async function downloadReportPdf(
  fileName: string,
  title: string,
  subtitle: string,
  components: readonly ReportDownloadComponent[],
  truncated: boolean,
  printHeader?: ReportPrintHeader | null,
  metadata?: ReportPdfMetadata,
) {
  const [{ jsPDF }, { default: autoTable }] = await Promise.all([
    import('jspdf'),
    import('jspdf-autotable'),
  ]);
  const pageWidth = reportPdfPageWidth(components);
  const pageHeight = reportPageHeight(pageWidth);
  const doc = new jsPDF({
    orientation: pageWidth < pageHeight ? 'portrait' : 'landscape',
    unit: 'mm',
    format: [pageWidth, pageHeight],
  });
  const templateHeaderHeight = printHeader?.template === organizationHeaderTemplate
    ? organizationHeaderDimensions(pageWidth).height
    : 0;
  const headerHeight = templateHeaderHeight || (printHeader ? 34 : 0);
  const pageTopMargin = headerHeight ? headerHeight + 9 : 20;
  const margin = 12;
  let top = drawReportTitle(
    doc,
    title,
    subtitle,
    headerHeight ? headerHeight + 10 : 18,
    pageWidth,
    metadata,
  );
  if (truncated) {
    doc.setFont('helvetica', 'normal');
    doc.setFontSize(8);
    doc.setTextColor(151, 100, 37);
    doc.text('Some results were limited for this report.', margin, top);
    top += 7;
  }

  let tableCount = 0;
  for (const [index, component] of components.entries()) {
    if (component.type === 'Table') {
      top = addPagedReportTable(autoTable, doc, {
        title: component.title,
        columns: component.columns,
        rows: component.rows,
        startY: top,
        pageTopMargin,
        sectionNumber: index + 1,
        startOnNewPage: tableCount > 0,
      });
      tableCount++;
    } else {
      top = drawChart(doc, component, top, pageTopMargin, index + 1) + 8;
    }
  }

  const pageCount = doc.getNumberOfPages();
  for (let page = 1; page <= pageCount; page++) {
    doc.setPage(page);
    doc.setDrawColor(192, 207, 226);
    doc.setLineWidth(0.3);
    doc.line(margin, pageHeight - 17, pageWidth - margin, pageHeight - 17);
    doc.setFont('helvetica', 'normal');
    doc.setFontSize(7.5);
    doc.setTextColor(...reportColors.muted);
    doc.text(`Generated on: ${new Intl.DateTimeFormat(undefined, {
      day: '2-digit',
      month: 'long',
      year: 'numeric',
    }).format(new Date())}   |   Enterprise Reporting Studio`, margin, pageHeight - 10);
    doc.text(`Page ${page} of ${pageCount}`, pageWidth - margin, pageHeight - 10, { align: 'right' });
    if (printHeader) drawReportPrintHeader(doc, printHeader, pageWidth);
  }

  doc.save(`${safeFileName(fileName)}.pdf`);
}
