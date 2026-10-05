import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function successfulBrowserBuild(run, jobs, repository) {
  if (run?.status !== 'completed' || run.name !== 'Build and release' || run.path !== '.github/workflows/build.yml' ||
      !['push', 'workflow_dispatch'].includes(run.event) || run.head_branch !== 'main' ||
      run.head_repository?.full_name !== repository || !/^[a-f0-9]{40}$/.test(run.head_sha || '') || !Array.isArray(jobs?.jobs))
    throw new Error('Untrusted or incomplete browser build identity.');
  const browser = jobs.jobs.filter(job => job.name === 'browser');
  // Other jobs can fail independently. Only this complete, unique browser job
  // establishes that its exact artifact passed the basic tests, build and packaging checks.
  return browser.length === 1 && browser[0].status === 'completed' && browser[0].conclusion === 'success';
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [runPath, jobsPath] = process.argv.slice(2);
  console.log(successfulBrowserBuild(JSON.parse(readFileSync(runPath)), JSON.parse(readFileSync(jobsPath)), process.env.GITHUB_REPOSITORY));
}
