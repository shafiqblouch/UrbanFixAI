import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { tokens } from "@urbanfix/tokens";
import {
  BottomTabs, Button, Card, EmptyState, ErrorState, KpiTile, LocaleProvider, OfflineState, Sheet, Sidebar,
  StatusChip, Table, TextField, createTranslator, directionOf, formatNumber, messages, type Locale, type Status,
} from "./index";

afterEach(cleanup);

const wrap = (locale: Locale, ui: React.ReactNode) => render(<LocaleProvider locale={locale}>{ui}</LocaleProvider>);

describe("locale core", () => {
  it("maps Urdu to rtl and English to ltr", () => {
    expect(directionOf("ur")).toBe("rtl");
    expect(directionOf("en")).toBe("ltr");
  });

  it("has the same keys in every language", () => {
    expect(Object.keys(messages.ur).sort()).toEqual(Object.keys(messages.en).sort());
  });

  it("interpolates and falls back to English then the key", () => {
    const t = createTranslator("ur", { en: { "x.only": "English only {n}" } });
    expect(t("x.only" as never, { n: 3 })).toBe("English only 3");
    expect(t("missing" as never)).toBe("missing");
    expect(createTranslator("en")("state.offline.queued", { count: 4 })).toBe("4 waiting to sync");
  });

  it("formats numbers with Latin digits in both languages", () => {
    expect(formatNumber("ur", 1250)).toBe("1,250");
    expect(formatNumber("en", 1250)).toBe("1,250");
  });

  it("throws when a component is used outside the provider", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => render(<Button>x</Button>)).toThrow(/LocaleProvider/);
    spy.mockRestore();
  });
});

describe.each<Locale>(["en", "ur"])("components in %s", (locale) => {
  const dir = locale === "ur" ? "rtl" : "ltr";

  it("sets lang and dir on the root", () => {
    const { container } = wrap(locale, <Button>ok</Button>);
    const root = container.querySelector(".uf-root")!;
    expect(root.getAttribute("dir")).toBe(dir);
    expect(root.getAttribute("lang")).toBe(locale);
  });

  it.each<Status>(["open", "assigned", "inProgress", "resolved", "rejected", "overdue"])("status chip %s shows icon and text", (status) => {
    const { container } = wrap(locale, <StatusChip status={status} />);
    expect(container.querySelector("svg")).not.toBeNull();
    expect(container.textContent).toBe(messages[locale][`status.${status}` as keyof typeof messages.en]);
  });

  it("links a text field to its hint and error", () => {
    wrap(locale, <TextField label="Name" hint="As on CNIC" error="Too short" required />);
    const input = screen.getByLabelText(/Name/);
    expect(input.getAttribute("aria-invalid")).toBe("true");
    const described = input.getAttribute("aria-describedby")!.split(" ").map((id) => document.getElementById(id)?.textContent);
    expect(described).toEqual(["As on CNIC", "Too short"]);
    expect(screen.getByRole("alert").textContent).toBe("Too short");
  });

  it("disables a loading button and announces it", () => {
    wrap(locale, <Button loading>Save</Button>);
    const button = screen.getByRole("button");
    expect((button as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByRole("status").getAttribute("aria-label")).toBe(messages[locale]["common.loading"]);
  });

  it("renders KPI tile, card and table with accessible names", () => {
    wrap(locale, (
      <>
        <KpiTile label="Open" value="12" tone="coral" />
        <Card title="Recent">body</Card>
        <Table caption="Jobs" rows={[{ id: "a", n: 3 }]} rowKey={(r) => r.id}
          columns={[{ key: "id", header: "ID", render: (r) => r.id }, { key: "n", header: "Count", render: (r) => r.n, numeric: true }]} />
      </>
    ));
    expect(screen.getByRole("group", { name: "Open" })).not.toBeNull();
    expect(screen.getByRole("heading", { name: "Recent" })).not.toBeNull();
    expect(screen.getByRole("columnheader", { name: "Count" }).className).toContain("uf-num");
  });

  it("marks the current nav item, badges counts and navigates", () => {
    const onNavigate = vi.fn();
    const items = [{ id: "a", label: "Complaints", count: 120 }, { id: "b", label: "Jobs", count: 0 }];
    wrap(locale, <><Sidebar items={items} currentId="a" onNavigate={onNavigate} /><BottomTabs items={items} currentId="b" /></>);
    const links = screen.getAllByRole("link", { name: /Complaints/ });
    expect(links[0].getAttribute("aria-current")).toBe("page");
    expect(links[0].textContent).toContain("99+");
    expect(screen.getAllByRole("link", { name: "Jobs" })).toHaveLength(2);
    fireEvent.click(links[0]);
    expect(onNavigate).toHaveBeenCalledWith("a");
  });

  it("opens a modal sheet, closes on Escape and the close button", () => {
    const onClose = vi.fn();
    wrap(locale, <Sheet open title="Details" onClose={onClose}>content</Sheet>);
    const dialog = screen.getByRole("dialog", { name: "Details" });
    fireEvent.keyDown(dialog, { key: "Escape" });
    fireEvent.click(screen.getByRole("button", { name: messages[locale]["common.close"] }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("renders empty, error and offline states", () => {
    const retry = vi.fn();
    wrap(locale, <><EmptyState /><ErrorState onRetry={retry} /><OfflineState queued={3} /></>);
    expect(screen.getByText(messages[locale]["state.empty.title"])).not.toBeNull();
    fireEvent.click(screen.getByRole("button", { name: messages[locale]["common.retry"] }));
    expect(retry).toHaveBeenCalled();
    expect(screen.getByRole("status").textContent).toContain("3");
  });
});

describe("design contract", () => {
  it("keeps the touch target at 48px", () => {
    expect(tokens.touchTarget).toBe(48);
  });
});
