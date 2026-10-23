import { useEffect, useId, useRef, type ReactNode } from "react";
import { useLocale } from "../locale";

export interface SheetProps {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
}

/** Bottom sheet on phones, centred dialog on wider screens (layout is CSS only). */
export function Sheet({ open, title, onClose, children }: SheetProps) {
  const { t } = useLocale();
  const titleId = useId();
  const panel = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    return () => previous?.focus();
  }, [open]);

  if (!open) return null;
  return (
    <div className="uf-sheet__backdrop" onClick={onClose}>
      <div
        ref={panel}
        className="uf-sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.key === "Escape" && onClose()}
      >
        <header className="uf-sheet__header">
          <h2 id={titleId} className="uf-sheet__title">{title}</h2>
          <button type="button" className="uf-btn uf-btn--ghost" onClick={onClose}>{t("common.close")}</button>
        </header>
        {children}
      </div>
    </div>
  );
}
