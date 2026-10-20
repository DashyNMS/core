# LibreNMS response fixtures

API responses from specific LibreNMS releases, for testing Core against each (#10). They follow
the shapes in that release's source (`api_functions.inc.php`, the models and migrations) and are
sanitised: names are `example.net`, addresses come from the documentation ranges, and the values
are invented. Never commit a response copied from a real network.

| Release | What changed that these cover |
|---|---|
| 26.9.1.1 | Legacy alerts: one row per rule and device (`/alerts`), no `fault_id` in the alert log; rules without per-entity settings, one using the since-removed `devices.last_ping_timetaken`; devices as every `devices` column, `override_sysContact` included |
| 26.10.0 | Per-entity faults (`/faults`, `alert_faults`), `fault_id` in the alert log; rules with `notify_per_entity` and `max_entities` (inherited and set); devices as the explicit device resource (librenms#20595), no `override_sysContact`, credentials nulled; eight cores faulting together and their alert log (with older legacy and per-fault rows); device groups, a group's members, LibreNMS's empty-group 404, and the per-resource 403s for an alert and a device group |

`LibreNms2610ScenarioTests` runs the #10 scenarios through Core against these. `LiveServerChecks` runs the
read-only part of the integration run against a real server when `DASHYNMS_LIVE_URL` and
`DASHYNMS_LIVE_TOKEN` are set (see the class for how), and skips otherwise.