import { BACKEND_URL } from './config';

// This app talks ONLY to the client's own backend. It never sees an Aspire token:
// the SSO handoff happens server-side. See docs/REDEEM_SSO_ANALYSIS.md.

// --- Client-owned authentication (user/pass here; could equally be the client's own SSO) ---
export async function login(username, password) {
  const res = await fetch(`${BACKEND_URL}/api/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.error || 'Login failed');
  return data;
}

// --- Reward catalogue rendered by the client app ---
export async function getRewards() {
  const res = await fetch(`${BACKEND_URL}/api/rewards`);
  if (!res.ok) throw new Error('Could not load rewards');
  return res.json();
}

// --- Redeem: the handoff ---
// mode 'jwt'  → client backend signs + POSTs to Aspire back-channel; returns a launch URL.
//               Fully silent; the token never touches the browser.
// mode 'saml' → returns a URL to the client's OWN IdP, which signs an assertion and
//               auto-POSTs it to Aspire's ACS. SAML's HTTP-POST binding needs the browser,
//               so this one cannot be back-channel.
// Either way the customer types nothing.
// `scenario` is a demo-only hook for negative testing.
export async function redeem(username, rewardId, mode, scenario) {
  const res = await fetch(`${BACKEND_URL}/api/redeem`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, rewardId, mode: mode || 'jwt', scenario: scenario || null }),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.error || 'Redeem failed');
  return data; // { launchUrl, reward }
}
