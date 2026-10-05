import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function pagesCredentialStatus(environment) {
  const required = ['CLOUDFLARE_ACCOUNT_ID', 'CLOUDFLARE_API_TOKEN'];
  const missing = required.filter(name => !environment[name]?.trim());
  return { configured: missing.length === 0, missing };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const status = pagesCredentialStatus(process.env);
  if (process.env.GITHUB_OUTPUT)
    appendFileSync(process.env.GITHUB_OUTPUT, `configured=${status.configured}\n`);
  if (status.configured) {
    console.log('Cloudflare Pages deployment credentials are configured.');
  } else {
    const message = `Cloudflare Pages deployment skipped: missing ${status.missing.join(', ')}. Native releases and browser build/QA are unaffected.`;
    console.log(message);
    if (process.env.GITHUB_STEP_SUMMARY)
      appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Pages deployment skipped\n\n${message}\n`);
  }
}
