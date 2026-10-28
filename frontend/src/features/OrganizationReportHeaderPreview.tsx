import { useRef, useState, type PointerEvent } from 'react';
import {
  organizationHeaderAspectRatio,
  isOrganizationHeaderTemplate,
} from './organizationReportHeader';
import {
  reportPrintHeaderElements,
  type ReportPrintHeader,
  type ReportPrintHeaderElement,
} from './reportPrintHeader';
import './OrganizationReportHeaderPreview.css';

interface OrganizationReportHeaderPreviewProps {
  header: Partial<ReportPrintHeader>;
  className?: string;
  interactive?: boolean;
  onElementChange?: (element: ReportPrintHeaderElement) => void;
}

const pageWidthMillimeters = 210;
const pageMarginMillimeters = 14;
const headerHeightMillimeters = pageWidthMillimeters / organizationHeaderAspectRatio;
const pageContentWidthMillimeters = pageWidthMillimeters - pageMarginMillimeters * 2;
const defaultLogoSize = 30;
const defaultLogoMaximumHeightMillimeters = 18;
const elementSizeBounds = {
  title: { minimum: 8, maximum: 36 },
  subtitle: { minimum: 7, maximum: 24 },
  logo: { minimum: 12, maximum: 60 },
} as const;

type Interaction = {
  pointerId: number;
  mode: 'move' | 'resize';
  startX: number;
  startY: number;
  element: ReportPrintHeaderElement;
};

function alignmentFor(x: number) {
  return x < 24 ? 'left' : x > 76 ? 'right' : 'center';
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.max(minimum, Math.min(maximum, value));
}

function logoDimensions(size: number, aspectRatio: number) {
  const maximumHeight = Math.min(headerHeightMillimeters - 2, 27);
  const defaultHeight = Math.min(
    defaultLogoMaximumHeightMillimeters,
    defaultLogoSize / aspectRatio,
  );
  const height = Math.min(maximumHeight, defaultHeight * size / defaultLogoSize);
  return { width: height * aspectRatio, height };
}

function boundedElement(
  element: ReportPrintHeaderElement,
  candidateX: number,
  candidateY: number,
  candidateSize: number,
  logoDataUrl?: string | null,
  logoAspectRatio = 1,
): ReportPrintHeaderElement {
  const bounds = elementSizeBounds[element.type];
  const size = clamp(candidateSize, bounds.minimum, bounds.maximum);
  const alignment = alignmentFor(candidateX);
  let minimumX = 0;
  let maximumX = 100;
  if (element.type === 'logo') {
    if (alignment === 'left') {
      maximumX = (pageContentWidthMillimeters - size) /
        pageContentWidthMillimeters * 100;
    } else if (alignment === 'right') {
      minimumX = size / pageContentWidthMillimeters * 100;
    } else {
      minimumX = size / 2 / pageContentWidthMillimeters * 100;
      maximumX = 100 - minimumX;
    }
  }
  const x = clamp(candidateX, minimumX, maximumX);
  const textHeight = size * 25.4 / 72;
  const verticalHalfSize = element.type === 'logo'
    ? (logoDataUrl ? logoDimensions(size, logoAspectRatio).height : 0) / 2
    : textHeight / 2;
  const minimumY = Math.max(
    0,
    (verticalHalfSize - 4) / 0.22,
  );
  const maximumY = Math.max(
    minimumY,
    (headerHeightMillimeters - verticalHalfSize - 4) / 0.22,
  );
  return {
    ...element,
    x,
    y: clamp(candidateY, minimumY, maximumY),
    size,
  };
}

export function OrganizationReportHeaderPreview({
  header,
  className = '',
  interactive = false,
  onElementChange,
}: OrganizationReportHeaderPreviewProps) {
  const interaction = useRef<Interaction | null>(null);
  const [logoAspectRatio, setLogoAspectRatio] = useState(1);
  const isLegacyTemplate = isOrganizationHeaderTemplate(header.template);

  return (
    <div className={`organization-report-header-preview ${className}`.trim()} aria-label="Organization report header">
      {isLegacyTemplate && header.logoDataUrl
        ? <img className="organization-report-header-preview-artwork" src={header.logoDataUrl} alt={header.title ?? ''} />
        : reportPrintHeaderElements(header).map((rawElement) => {
          const element = boundedElement(
            rawElement,
            rawElement.x,
            rawElement.y,
            rawElement.size,
            header.logoDataUrl,
            logoAspectRatio,
          );
          const x = (pageMarginMillimeters +
            pageContentWidthMillimeters * element.x / 100) /
            pageWidthMillimeters * 100;
          const y = (4 + element.y * 0.22) / headerHeightMillimeters * 100;
          const alignment = alignmentFor(element.x);
          const logoSize = element.type === 'logo'
            ? logoDimensions(element.size, logoAspectRatio)
            : null;
          const transform = alignment === 'left'
            ? 'translateY(-50%)'
            : alignment === 'right' ? 'translate(-100%, -50%)' : 'translate(-50%, -50%)';
          const textWidth = alignment === 'left'
            ? pageWidthMillimeters - pageMarginMillimeters -
              (pageMarginMillimeters + pageContentWidthMillimeters * element.x / 100)
            : alignment === 'right'
              ? pageMarginMillimeters + pageContentWidthMillimeters * element.x / 100 -
                pageMarginMillimeters
              : Math.min(
                  pageMarginMillimeters + pageContentWidthMillimeters * element.x / 100 -
                    pageMarginMillimeters,
                  pageWidthMillimeters - pageMarginMillimeters -
                    (pageMarginMillimeters + pageContentWidthMillimeters * element.x / 100),
                ) * 2;
          const style = {
            left: `${x}%`,
            top: `${y}%`,
            transform,
            fontSize: `${element.size * 100 / (pageWidthMillimeters * 72 / 25.4)}cqw`,
            ...(element.type === 'logo'
              ? {
                  width: `${(logoSize?.width ?? 0) / pageWidthMillimeters * 100}%`,
                  height: `${(logoSize?.height ?? 0) / headerHeightMillimeters * 100}%`,
                }
              : {
                  width: `${Math.max(0, textWidth) / pageWidthMillimeters * 100}%`,
                }),
          };

          const handlePointerDown = (
            event: PointerEvent<HTMLElement>,
            mode: Interaction['mode'],
          ) => {
            if (!interactive || !onElementChange) return;
            event.preventDefault();
            event.currentTarget
              .closest('.organization-report-header-preview-target')
              ?.setPointerCapture(event.pointerId);
            interaction.current = {
              pointerId: event.pointerId,
              mode,
              startX: event.clientX,
              startY: event.clientY,
              element,
            };
          };

          const handlePointerMove = (event: PointerEvent<HTMLDivElement>) => {
            const active = interaction.current;
            if (!active || active.pointerId !== event.pointerId || !onElementChange) return;
            const container = event.currentTarget.parentElement;
            if (!container) return;
            const bounds = container.getBoundingClientRect();
            if (bounds.width <= 0 || bounds.height <= 0) return;
            const deltaX = (event.clientX - active.startX) / bounds.width *
              pageWidthMillimeters;
            const deltaY = (event.clientY - active.startY) / bounds.height *
              headerHeightMillimeters;
            const candidateX = active.element.x +
              deltaX / pageContentWidthMillimeters * 100;
            const candidateY = active.element.y + deltaY / 0.22;
            const candidateSize = active.mode === 'resize'
              ? active.element.size + deltaX
              : active.element.size;
            onElementChange(boundedElement(
              active.element,
              candidateX,
              candidateY,
              candidateSize,
              header.logoDataUrl,
              logoAspectRatio,
            ));
          };

          const handlePointerEnd = (event: PointerEvent<HTMLDivElement>) => {
            if (interaction.current?.pointerId === event.pointerId) {
              interaction.current = null;
            }
          };

          const text = element.type === 'title' ? header.title : header.subtitle;
          if (element.type !== 'logo' && !text) return null;
          if (element.type === 'logo' && !header.logoDataUrl) return null;
          return <div
            key={element.type}
            className={`organization-report-header-preview-item organization-report-header-preview-target${interactive ? ' is-editable' : ''}`}
            style={style}
            onPointerDown={(event) => handlePointerDown(event, 'move')}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerEnd}
            onPointerCancel={handlePointerEnd}
          >
            {element.type === 'logo'
              ? <img
                  className="organization-report-header-preview-logo"
                  src={header.logoDataUrl!}
                  alt=""
                  draggable={false}
                  onLoad={(event) => {
                    const { naturalWidth, naturalHeight } = event.currentTarget;
                    if (naturalWidth > 0 && naturalHeight > 0) {
                      setLogoAspectRatio(naturalWidth / naturalHeight);
                    }
                  }}
                />
              : <span className={`organization-report-header-preview-text is-${element.type} align-${alignment}`}>{text}</span>}
            {interactive && <button
              className="organization-report-header-preview-resize"
              type="button"
              aria-label={`Resize ${element.type === 'logo' ? 'logo' : 'heading'}`}
              onKeyDown={(event) => {
                if (!onElementChange || !['ArrowUp', 'ArrowRight', 'ArrowDown', 'ArrowLeft'].includes(event.key)) return;
                event.preventDefault();
                const delta = event.key === 'ArrowUp' || event.key === 'ArrowRight' ? 1 : -1;
                onElementChange(boundedElement(
                  element,
                  element.x,
                  element.y,
                  element.size + delta,
                  header.logoDataUrl,
                  logoAspectRatio,
                ));
              }}
              onPointerDown={(event) => {
                event.stopPropagation();
                handlePointerDown(event, 'resize');
              }}
            />}
          </div>;
        })}
    </div>
  );
}
