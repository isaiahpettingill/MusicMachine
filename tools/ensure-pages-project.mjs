const account = process.env.CLOUDFLARE_ACCOUNT_ID;
const token = process.env.CLOUDFLARE_API_TOKEN;
if (!account || !token) throw new Error('Set CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN in repository Actions secrets.');
if (!/^[a-f0-9]{32}$/i.test(account)) throw new Error('Invalid Cloudflare account ID.');
const url = `https://api.cloudflare.com/client/v4/accounts/${account}/pages/projects`;
const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
const existing = await fetch(url + '/music-machine-synth', { headers });
if (existing.ok) {
    const { result } = await existing.json();
    if (result.production_branch !== 'main') throw new Error('The existing music-machine-synth Pages project must use main as its production branch.');
    if (result.subdomain !== 'music-machine-synth.pages.dev') throw new Error('The requested music-machine-synth.pages.dev address is not available on this project.');
    if (result.latest_deployment) {
        const identity = await fetch('https://' + result.subdomain + '/build.json', { signal: AbortSignal.timeout(20000) });
        let build = null;
        try { if (identity.ok) build = await identity.json(); } catch { /* Refuse an unrelated existing site. */ }
        if (build?.product !== 'MusicMachine' || build?.repository !== process.env.GITHUB_REPOSITORY)
            throw new Error('Existing music-machine-synth Pages project cannot be verified as this repository. Review it before deployment; nothing was overwritten.');
    }
    console.log('Using existing music-machine-synth Pages project.');
} else {
    if (existing.status !== 404) throw new Error(`Cloudflare project lookup failed (HTTP ${existing.status}). Check token permissions and account ID.`);
    const created = await fetch(url, {
        method: 'POST', headers,
        body: JSON.stringify({ name: 'music-machine-synth', production_branch: 'main' })
    });
    if (!created.ok) throw new Error(`Cloudflare project creation failed (HTTP ${created.status}).`);
    const { result } = await created.json();
    if (result.subdomain !== 'music-machine-synth.pages.dev') throw new Error('Cloudflare assigned a different address; music-machine-synth.pages.dev is unavailable.');
    console.log('Created music-machine-synth.pages.dev.');
}
