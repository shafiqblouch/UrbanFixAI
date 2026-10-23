import type { ButtonHTMLAttributes, ReactNode } from "react";
import { useLocale } from "../locale";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: "primary" | "secondary" | "danger" | "ghost";
  loading?: boolean;
  children: ReactNode;
}

export function Button({ variant = "primary", loading = false, disabled, className, children, type = "button", ...rest }: ButtonProps) {
  const { t } = useLocale();
  return (
    <button
      {...rest}
      type={type}
      className={["uf-btn", `uf-btn--${variant}`, className].filter(Boolean).join(" ")}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
    >
      {loading && <span className="uf-spinner" role="status" aria-label={t("common.loading")} />}
      {children}
    </button>
  );
}
