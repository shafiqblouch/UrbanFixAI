import { Alert, Button, Card, SelectField, TextAreaField } from "@urbanfix/ui";
import { useState, type FormEvent } from "react";
import { api, ok, type S } from "../api";
import { ErrorNote } from "../components";
import { useAction } from "../hooks";
import { useFormat, useT } from "../i18n";

const RATINGS = [5, 4, 3, 2, 1] as const;

/** The resident's rating and comment once the work is done (JB-08); staff see it read-only. */
export function FeedbackCard({ complaint, feedback, canGive, onSaved }: {
  complaint: S["ComplaintDto"]; feedback: S["FeedbackDto"] | null | undefined; canGive: boolean; onSaved: () => void;
}) {
  const t = useT();
  const f = useFormat();
  const action = useAction();
  const [rating, setRating] = useState(String(feedback?.rating ?? ""));
  const [fixed, setFixed] = useState(feedback?.confirmedFixed ?? true);
  const [comment, setComment] = useState(feedback?.comment ?? "");
  const finished = complaint.status === "Resolved" || complaint.status === "Closed";

  if (!canGive) {
    if (!feedback) return null;
    return (
      <Card title={t("feedback.title")}>
        <dl className="facts">
          <dt>{t("feedback.rating")}</dt><dd>{t("feedback.stars", { rating: f.number(feedback.rating) })}</dd>
          <dt>{t("feedback.fixed")}</dt><dd>{feedback.confirmedFixed ? t("yes") : t("no")}</dd>
          {feedback.comment && <><dt>{t("feedback.comment")}</dt><dd>{feedback.comment}</dd></>}
        </dl>
      </Card>
    );
  }
  if (!finished) return null;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await ok(api.POST("/complaints/{id}/feedback", { params: { path: { id: complaint.id } }, body: { rating: Number(rating), confirmedFixed: fixed, comment: comment.trim() || null } }));
      onSaved();
    });
  };
  return (
    <Card title={t("feedback.title")}>
      <form className="stack" onSubmit={submit}>
        {feedback && <Alert tone="success">{t("feedback.thanks")}</Alert>}
        <SelectField label={t("feedback.rating")} placeholder={t("choose")} value={rating} onChange={(e) => setRating(e.target.value)}
          options={RATINGS.map((r) => ({ value: String(r), label: t("feedback.stars", { rating: r }) }))} />
        <label className="row"><input type="checkbox" checked={fixed} onChange={(e) => setFixed(e.target.checked)} /> {t("feedback.fixedQuestion")}</label>
        <TextAreaField label={t("feedback.comment")} maxLength={1000} value={comment} onChange={(e) => setComment(e.target.value)} />
        <ErrorNote error={action.error} />
        <div className="row"><Button type="submit" loading={action.busy} disabled={rating === ""}>{t(feedback ? "feedback.update" : "feedback.send")}</Button></div>
      </form>
    </Card>
  );
}
