// Service worker for push notifications (FD-16). It does one job: show a notification when the server sends one, and open the right
// screen when it is clicked. The message arrives encrypted and is decrypted by the browser, so it can carry the text itself.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));

self.addEventListener("push", (event) => {
  let message = { title: "UrbanFix", body: "", link: "/" };
  try { if (event.data) message = { ...message, ...event.data.json() }; } catch { /* not JSON: keep the plain text below */ }
  if (event.data && !message.body) message.body = event.data.text();
  event.waitUntil(
    self.registration.showNotification(message.title, {
      body: message.body,
      tag: message.id || undefined, // the same message never shows twice
      data: { link: message.link || "/" },
      icon: "/favicon.ico",
    }),
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const link = (event.notification.data && event.notification.data.link) || "/";
  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((windows) => {
      for (const client of windows) {
        if ("focus" in client) {
          client.navigate(link);
          return client.focus();
        }
      }
      return self.clients.openWindow(link);
    }),
  );
});
