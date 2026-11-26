# Security policy

Thanks for helping keep DashyNMS Core and the people who use it safe.

## Reporting a vulnerability

Please **don't open a public issue** for a security problem.

Report it privately instead: on this repository, go to **Security → Report a vulnerability**. Only the maintainer can see the report.

It helps to include:

- what the problem is and what someone could do with it
- the steps to reproduce it, the package version, and the app you used it in
- anything you've found about how to fix it

## What happens next

- You'll get a reply within **5 working days** confirming the report has been received.
- We'll keep you updated while it's investigated and let you know when a fix is released.
- Once it's fixed, you're welcome to be credited in the release notes, or to stay anonymous.

Please give us a reasonable chance to release a fix before you share details publicly.

## Supported versions

Security fixes go into `main` first, so the next edge package has them as soon as they merge, and then into the **next release**. Please check the problem is still there on the newest package before reporting.

You're welcome to test Core under its licence for security problems, against your own LibreNMS, Unimus and Graylog. Please don't test against systems you don't own.

## Scope

**In scope:** the `DashyNMS.Core` library and package, including how it handles credentials and certificates, reads responses, and downloads updates and sensor definitions.

**Out of scope:**

- problems in LibreNMS itself. Please report those to the [LibreNMS project](https://github.com/librenms/librenms/security).
- problems in one app only. Please report those to [desktop](https://github.com/DashyNMS/desktop/security), [mobile](https://github.com/DashyNMS/mobile/security) or [MCP](https://github.com/DashyNMS/mcp/security).
- how a particular LibreNMS server is set up or secured

Known, open security issues are tracked publicly once it's safe to do so.

## Repository checks

[Repository standards](docs/REPOSITORY-STANDARDS.md) describes dependency audits,
secret scanning, CodeQL analysis and publication gates. Passing a scan does not
guarantee that no vulnerability exists. Report suspected vulnerabilities privately.
