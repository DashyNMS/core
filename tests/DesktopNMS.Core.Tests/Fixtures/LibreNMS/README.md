# LibreNMS response fixtures

API responses from specific LibreNMS releases, for testing Core against each (#10). They follow
the shapes in that release's source (`api_functions.inc.php`, the models and migrations) and are
sanitised: names are `example.net`, addresses come from the documentation ranges, and the values
are invented. Never commit a response copied from a real network.

| Release | What changed that these cover |
|---|---|
| 26.9.1.1 | Legacy alerts: one row per rule and device (`/alerts`), no `fault_id` in the alert log |
| 26.10.0 | Per-entity faults (`/faults`, `alert_faults`), `fault_id` in the alert log |