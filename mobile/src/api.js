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

// --- Redeem: the silent handoff ---
// The client backend signs the JWT and hands it to Aspire back-channel, then returns a
// one-time launch URL. The customer sees no login — just the reward page opening.
// `scenario` is a demo-only hook for negative testing.
export async function redeem(username, rewardId, scenario) {
  const res = await fetch(`${BACKEND_URL}/api/redeem`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, rewardId, scenario: scenario || null }),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.error || 'Redeem failed');
  return data; // { launchUrl, reward }
}
