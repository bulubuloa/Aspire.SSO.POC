using System.Text.Json;
namespace Aspire.Sso;

// Server-rendered pages for the Aspire-hosted RDP experience.
// Styling comes from wwwroot/aspire-theme.css (tokens lifted from dining.aspireasia.net).
public static class Pages
{
    private const string Head = """
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width,initial-scale=1">
    <link rel="icon" href="/favicon.svg" type="image/svg+xml"/>
    <link rel="stylesheet" href="/aspire-theme.css"/>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/fontawesome-free-6.2.1@6.2.1/css/all.min.css"/>
    """;

    private const string Header = """
    <div class="aspire-header">
      <div class="wordmark"><b>ASPIRE</b><span>LIFESTYLES</span></div>
      <span class="env">RDP · UAT</span>
    </div>
    """;

    // Landing page for a silent Redeem handoff — deep-linked to one reward via the `target` claim.
    // The customer arrives here already authenticated, having seen no login screen.
    // returnUrl = the client's deep link, agreed at onboarding. Closing hands the customer back
    // to their app: postMessage when framed (web demo), otherwise navigate to the deep link so a
    // native in-app browser dismisses itself.
    public static string Redeem(SessionStore.AspireSession s, Reward r, string returnUrl) => $$"""
    <!doctype html><html lang="en"><head>{{Head}}<title>Redeem — {{r.Title}}</title></head><body>
    {{Header}}
    <div class="wrap" style="max-width:520px">
      <div class="card">
        <div class="caption" style="text-align:center">
          <div style="font-size:34px;color:var(--ink);margin-bottom:6px"><i class="fa-solid fa-{{r.Icon}}"></i></div>
          <h2 style="font-size:22px">{{r.Title}}</h2>
          <div style="color:#6b6448;font-size:14px">{{r.Detail}}</div>
        </div>

        <div style="display:flex;align-items:center;gap:10px;margin-bottom:16px">
          <span class="pill"><i class="fa-solid fa-user-check"></i> {{s.DisplayName}}</span>
          <span class="pill" style="background:#cef2e7;color:#08845f"><i class="fa-solid fa-bolt"></i> AUTO SIGNED IN</span>
        </div>

        <div class="grid">
          <div class="kv"><b>COST</b><span>{{r.Points}} pts</span></div>
          <div class="kv"><b>BALANCE</b><span>2,450 pts</span></div>
        </div>

        <button class="btn" id="confirm" style="width:100%;margin-top:18px">
          <i class="fa-solid fa-check"></i> CONFIRM REDEMPTION
        </button>

        <div id="done" style="display:none;margin-top:38px">
          <div class="kv" style="text-align:center;background:#cef2e7;border:1px solid #9ee4cf">
            <b style="color:#08845f">REDEEMED</b>
            <span>Confirmation sent to {{s.Email}}</span>
          </div>
          <button class="btn btn-dark" id="close" style="width:100%;margin-top:12px">
            <i class="fa-solid fa-arrow-left"></i> CLOSE AND RETURN TO THE APP
          </button>
        </div>

        <p class="muted" style="text-align:center;margin:14px 0 0;font-size:12px">
          You were signed in automatically by your provider — no Aspire password needed.
        </p>
      </div>
    </div>
    <script>
      var REWARD = {{RewardJson(r)}};
      document.getElementById('confirm').onclick = function () {
        this.style.display = 'none';
        document.getElementById('done').style.display = 'block';
      };
      document.getElementById('close').onclick = function () {
        var msg = { type: 'aspire:redeemed', reward: REWARD.id };
        // Framed by the client app (web demo) — tell the parent to close us.
        if (window.parent && window.parent !== window) { window.parent.postMessage(msg, '*'); return; }
        // Native in-app browser — the deep link is what dismisses it.
        var back = {{ReturnUrlJson(returnUrl)}};
        if (back) location.href = back + '?reward=' + encodeURIComponent(REWARD.id);
      };
    </script>
    </body></html>
    """;

    private static string RewardJson(Reward r) => JsonSerializer.Serialize(new { id = r.Id, title = r.Title });
    private static string ReturnUrlJson(string returnUrl) => JsonSerializer.Serialize(returnUrl);

    public static string Benefit(SessionStore.AspireSession s) => $$"""
    <!doctype html><html lang="en"><head>{{Head}}<title>Aspire Benefits</title></head><body>
    {{Header}}
    <div class="wrap">
      <div class="card">
        <div class="caption">
          <span class="pill"><i class="fa-solid fa-shield-halved"></i> SIGNED IN VIA {{s.Via}} SSO</span>
          <h2 style="margin-top:10px;font-size:24px">Welcome, {{s.DisplayName}}</h2>
          <div style="color:#6b6448;font-size:14px">Your identity was verified by your provider — no separate Aspire password needed.</div>
        </div>
        <div class="grid">
          <div class="kv"><b>USER (SUB)</b><span>{{s.Sub}}</span></div>
          <div class="kv"><b>EMAIL</b><span>{{s.Email}}</span></div>
          <div class="kv"><b>COUNTRY / MARKET</b><span>{{s.Country}}</span></div>
          <div class="kv"><b>PROGRAM</b><span>{{s.Program}}</span></div>
        </div>
      </div>

      <div class="card">
        <h2>Your benefit experience</h2>
        <div class="benefit-list">
          <div class="benefit"><div class="ico"><i class="fa-solid fa-car-burst"></i></div>
            <div><b>Roadside Assistance</b><div class="muted">24/7 cover · tap to request help</div></div></div>
          <div class="benefit"><div class="ico"><i class="fa-solid fa-heart-pulse"></i></div>
            <div><b>Health &amp; Wellness</b><div class="muted">Teleconsult · claims · rewards</div></div></div>
          <div class="benefit"><div class="ico"><i class="fa-solid fa-plane-departure"></i></div>
            <div><b>Travel Privileges</b><div class="muted">Lounge access · trip cover</div></div></div>
        </div>
        <p style="margin-top:18px">
          <button class="btn btn-dark" onclick="fetch('/aspire/logout',{method:'POST'}).then(()=>location.href='/')">
            <i class="fa-solid fa-right-from-bracket"></i> LOG OUT
          </button>
        </p>
      </div>
    </div>
    <style>
      .benefit { display:flex; gap:14px; align-items:center; padding:13px 0; border-top:1px solid var(--border); }
      .benefit-list > .benefit:first-child { border-top:0; }
      .benefit .ico { width:44px; height:44px; border-radius:var(--radius-sm); background:var(--sand);
                      display:grid; place-items:center; font-size:17px; color:var(--ink); flex:none; }
      .benefit b { color:var(--ink); }
    </style>
    </body></html>
    """;

    // Browser front-channel POST result: set the session cookie client-side, then land on the benefit page.
    public static string SigningIn(SessionStore.AspireSession s) => $$"""
    <!doctype html><html><head>{{Head}}<title>Signing you in…</title></head><body>
    {{Header}}
    <div class="wrap"><div class="card">
      <i class="fa-solid fa-circle-notch fa-spin" style="color:var(--red)"></i>
      Signing you in via {{s.Via}}…
    </div></div>
    <script>
      document.cookie = "aspire_session={{s.Id}}; path=/; SameSite=Lax";
      location.replace("/aspire/benefit");
    </script></body></html>
    """;

    public static string Error(string message) => $$"""
    <!doctype html><html lang="en"><head>{{Head}}<title>SSO rejected</title></head><body>
    {{Header}}
    <div class="wrap"><div class="card">
      <h2><i class="fa-solid fa-circle-exclamation" style="color:var(--danger)"></i> SSO handoff rejected</h2>
      <p class="muted">Aspire could not create a session from the token it received.</p>
      <div class="kv"><b>REASON</b><span>{{message}}</span></div>
      <p style="margin-top:18px"><a class="btn btn-ghost" href="/"><i class="fa-solid fa-arrow-left"></i> BACK TO THE DEMO LAUNCHER</a></p>
    </div></div></body></html>
    """;
}
