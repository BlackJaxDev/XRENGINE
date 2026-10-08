# Azure deployment preparation

Status: deployment templates only. No Azure resources were created. The native UI
sample cannot pass the current engine browser exporter. Complete and validate the
shared native UI render, input, font, and cook paths before packaging this site.

## Account connection

Microsoft provides an [Azure MCP Server](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/overview).
Merge the `servers` entry from `mcp.example.json` into the host's MCP configuration.
For VS Code, this is `.vscode/mcp.json`. Preserve the existing XRENGINE server entry.
The example requires Node.js and follows Microsoft's documented NPX setup. Pin a
reviewed server version when you establish the deployment environment.

Sign in on the machine that runs the MCP server or deployment CLI:

```powershell
az login
az account show --query '{subscription:name,id:id,tenant:tenantId}'
```

Select the intended subscription with `az account set --subscription <subscription-id>`.
The account needs permissions for the target resources. No credentials belong in
source files. A local MCP server does not automatically connect to another chat host.

## Windows App Service

This template targets a **dedicated Windows App Service web app**, with IIS serving
the static browser bundle. It does not apply to Linux App Service. Resource creation,
region, plan pricing, DNS ownership, and account selection remain unconfigured.
Do not use an existing application that serves unrelated content.

After the engine can publish this native scene, use the existing editor project
build action with `BrowserWebGPU`. Package its complete player output, including
the runtime, shaders, launch descriptor, and cooked assets:

```powershell
pwsh Samples/PhenotypeWebsite/Azure/Prepare-AppService.ps1 `
  -PublishedSite <project-browser-output> `
  -OutputZip <package-directory>/phenotype-website.zip
```

The script preserves the source output. It rejects the known developer harness
and a launch descriptor without a cooked world. It does not certify the contents
of that world. It adds IIS MIME mappings for WASM, WGSL, and binary content.
The initial cache policy revalidates all files. Add measured immutable-asset and
compression policies after the first successful launch. Missing URLs retain 404
responses; no catch-all route returns HTML for a missing shader or asset.

For an authenticated account and a dedicated Windows web app, the deployment
commands are:

```powershell
az webapp update --resource-group <resource-group> --name <app-name> --https-only true
az webapp deploy --resource-group <resource-group> --name <app-name> `
  --src-path <package-directory>/phenotype-website.zip --type zip
```

Use the default Azure hostname for first verification. Then add
`phenotypeengine.com`, verify DNS ownership, and configure its HTTPS certificate.
DNS records must use the values returned for the actual resource. ZIP deployment
can replace files and restart the target app. Retain the previous package for rollback.

## Hosting alternative

[Azure Static Web Apps](https://learn.microsoft.com/en-us/azure/static-web-apps/overview)
also fits this static workload. It provides distributed static hosting, custom
domains, and managed certificates. Select that service before preparing its own
deployment configuration; it does not use this IIS `web.config`.

## Verification still required

- Build the sample and attach it to an active native world. Verify the title,
  panel, button, and status change in the engine editor.
- Verify the same components after the real browser world is cooked and loaded.
- Inspect the network responses for the runtime, WGSL, and every cooked payload.
- Verify pointer and touch input, resize, loading failures, and unsupported WebGPU.
- Verify HTTPS, MIME types, missing-asset 404 responses, and the public Azure URL.

Sources: [MCP setup and authentication](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/get-started/tools/visual-studio-code),
[App Service ZIP deployment](https://learn.microsoft.com/en-us/azure/app-service/deploy-zip),
[WebAssembly hosting](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly?view=aspnetcore-10.0).
