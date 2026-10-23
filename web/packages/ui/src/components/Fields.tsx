import { useId, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";
import { useLocale } from "../locale";

interface Common {
  label: string;
  hint?: string;
  error?: string;
}

function Shell({ id, label, hint, error, required, className, children }: Common & { id: string; required?: boolean; className?: string; children: ReactNode }) {
  const { t } = useLocale();
  return (
    <div className={["uf-field", error && "uf-field--invalid", className].filter(Boolean).join(" ")}>
      <label className="uf-field__label" htmlFor={id}>
        {label}
        {required && <span className="uf-field__required"> ({t("common.required")})</span>}
      </label>
      {children}
      {hint && <p id={`${id}-hint`} className="uf-field__hint">{hint}</p>}
      {error && <p id={`${id}-error`} className="uf-field__error" role="alert">{error}</p>}
    </div>
  );
}

const describedBy = (id: string, hint?: string, error?: string) => [hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(" ") || undefined;

export interface SelectOption {
  value: string;
  label: string;
}

export interface SelectFieldProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, "id" | "children">, Common {
  options: SelectOption[];
  /** Text of the empty choice; omit to have no empty choice. */
  placeholder?: string;
}

export function SelectField({ label, hint, error, options, placeholder, required, className, ...rest }: SelectFieldProps) {
  const id = useId();
  return (
    <Shell id={id} label={label} hint={hint} error={error} required={required} className={className}>
      <select {...rest} id={id} required={required} className="uf-field__input" aria-invalid={error ? true : undefined} aria-describedby={describedBy(id, hint, error)}>
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </Shell>
  );
}

export interface TextAreaFieldProps extends Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, "id">, Common {}

export function TextAreaField({ label, hint, error, required, className, rows = 4, ...rest }: TextAreaFieldProps) {
  const id = useId();
  return (
    <Shell id={id} label={label} hint={hint} error={error} required={required} className={className}>
      <textarea {...rest} id={id} rows={rows} required={required} className="uf-field__input uf-field__input--multiline" aria-invalid={error ? true : undefined} aria-describedby={describedBy(id, hint, error)} />
    </Shell>
  );
}

/** A labelled bar showing a share of a whole (value between 0 and 1). */
export function ProgressBar({ label, value, valueText }: { label: string; value: number; valueText?: string }) {
  const pct = Math.round(Math.min(Math.max(value, 0), 1) * 100);
  return (
    <div className="uf-progress">
      <div className="uf-progress__head">
        <span>{label}</span>
        <span>{valueText ?? `${pct}%`}</span>
      </div>
      <div className="uf-progress__track" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct}>
        <div className="uf-progress__fill" style={{ inlineSize: `${pct}%` }} />
      </div>
    </div>
  );
}

export function Alert({ tone = "info", children }: { tone?: "info" | "success" | "warning" | "danger"; children: ReactNode }) {
  return <div className={`uf-alert uf-alert--${tone}`} role={tone === "danger" ? "alert" : "status"}>{children}</div>;
}
