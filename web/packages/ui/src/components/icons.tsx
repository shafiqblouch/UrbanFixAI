import type { ReactNode } from "react";

const Svg = ({ children }: { children: ReactNode }) => (
  <svg className="uf-icon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false"
    fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
    {children}
  </svg>
);

// Each status has its own shape so state is never conveyed by colour alone.
export const Icons = {
  open: () => <Svg><circle cx="8" cy="8" r="5.5" /></Svg>,
  assigned: () => <Svg><circle cx="8" cy="5.5" r="2.5" /><path d="M3 13.5c.8-2.6 2.8-3.5 5-3.5s4.2.9 5 3.5" /></Svg>,
  inProgress: () => <Svg><circle cx="8" cy="8" r="5.5" /><path d="M8 4.5V8l2.5 1.5" /></Svg>,
  resolved: () => <Svg><path d="M3 8.5l3.2 3L13 4.5" /></Svg>,
  rejected: () => <Svg><path d="M4 4l8 8M12 4l-8 8" /></Svg>,
  overdue: () => <Svg><path d="M8 2.5l6 10.5H2z" /><path d="M8 7v2.5M8 11.3v.2" /></Svg>,
  offline: () => <Svg><path d="M2 6.5a9 9 0 0112 0M4.5 9a5.5 5.5 0 017 0M7 11.5a2 2 0 012 0M2.5 2.5l11 11" /></Svg>,
  error: () => <Svg><circle cx="8" cy="8" r="5.5" /><path d="M8 4.8v3.8M8 10.8v.2" /></Svg>,
  empty: () => <Svg><path d="M2.5 9l2-5.5h7l2 5.5v4h-11z" /><path d="M2.5 9h3.5l.8 1.5h2.4L10 9h3.5" /></Svg>,
};
