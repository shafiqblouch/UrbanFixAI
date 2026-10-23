import { EmptyState } from "@urbanfix/ui";
import { PageHeader } from "../components";
import { useT } from "../i18n";

/** Shown to roles that have no screens in the portal yet. */
export function WelcomePage() {
  const t = useT();
  return (
    <>
      <PageHeader title={t("app.name")} />
      <EmptyState title={t("welcome.title")} body={t("welcome.body")} />
    </>
  );
}
