import type { HTMLAttributes, ReactNode } from "react";

export interface CardProps extends Omit<HTMLAttributes<HTMLElement>, "title"> {
  title?: ReactNode;
  actions?: ReactNode;
}

export function Card({ title, actions, className, children, ...rest }: CardProps) {
  return (
    <section {...rest} className={["uf-card", className].filter(Boolean).join(" ")}>
      {(title || actions) && (
        <header className="uf-card__header">
          {title && <h2 className="uf-card__title">{title}</h2>}
          {actions && <div className="uf-card__actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}
