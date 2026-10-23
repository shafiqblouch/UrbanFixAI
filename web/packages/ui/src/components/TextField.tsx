import { useId, type InputHTMLAttributes } from "react";
import { useLocale } from "../locale";

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id"> {
  label: string;
  hint?: string;
  error?: string;
}

export function TextField({ label, hint, error, required, className, ...rest }: TextFieldProps) {
  const { t } = useLocale();
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const describedBy = [hint && hintId, error && errorId].filter(Boolean).join(" ") || undefined;
  return (
    <div className={["uf-field", error && "uf-field--invalid", className].filter(Boolean).join(" ")}>
      <label className="uf-field__label" htmlFor={id}>
        {label}
        {required && <span className="uf-field__required"> ({t("common.required")})</span>}
      </label>
      <input {...rest} id={id} className="uf-field__input" required={required}
        aria-invalid={error ? true : undefined} aria-describedby={describedBy} />
      {hint && <p id={hintId} className="uf-field__hint">{hint}</p>}
      {error && <p id={errorId} className="uf-field__error" role="alert">{error}</p>}
    </div>
  );
}
