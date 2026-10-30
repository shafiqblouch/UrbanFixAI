# Maps

The portal draws complaints and technician positions on an OpenStreetMap map (Leaflet, no API key). The browser needs internet access to load the map tiles.

## Where positions come from

| Source | What is stored |
| --- | --- |
| Mobile app, resident | The GPS fix from "Use my location" is sent with the complaint (`latitude`, `longitude` on `POST /complaints`). |
| WhatsApp | A location pin sent in the chat is stored on the complaint, whether or not it matched a known place. |
| Mobile app, technician | Starting the work and finishing it always send a fix. A note sends one when "include GPS" is ticked. |
| Older complaints | Migration `0029_complaint_coordinates.sql` copies coordinates out of the free-text location when it has them. |

A complaint entered in the portal, or one where the reporter refused location permission, has no position and is not drawn.

## Where they show

- **Map** (menu, Manager / Supervisor / Office / Support): `GET /map`. One dot per complaint with a position, coloured by status, plus the latest position of each technician on a job still being worked. It takes the same filters and follows the same visibility rules as the complaint list, refreshes every 30 seconds, and says how many complaints came without a position.
- **Complaint page**: a small map of where it was reported from.
- **Job page**: the complaint pin, and the technician's positions numbered in the order sent and joined by a line, plus any evidence photo that carries a position.
