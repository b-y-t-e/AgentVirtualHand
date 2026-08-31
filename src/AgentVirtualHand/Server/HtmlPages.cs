namespace AgentVirtualHand.Server;

/// <summary>Minimalne strony HTML - landing oraz formularz parowania dla telefonu po zeskanowaniu QR.</summary>
public static class HtmlPages
{
    private const string Style = """
        <style>
          :root { color-scheme: dark; }
          body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
                 background:#0e1116; color:#e6e9ef;
                 font:15px/1.55 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; }
          .card { width:min(560px,92vw); background:#161a22; border:1px solid #232935; border-radius:16px;
                  padding:28px 30px; box-shadow:0 24px 60px rgba(0,0,0,.45); }
          h1 { margin:0 0 4px; font-size:19px; letter-spacing:-.01em; }
          p.sub { margin:0 0 22px; color:#8b95a7; font-size:13px; }
          label { display:block; font-size:12px; text-transform:uppercase; letter-spacing:.08em; color:#8b95a7; margin-bottom:8px; }
          input { width:100%; box-sizing:border-box; padding:13px 14px; border-radius:10px; border:1px solid #2b3242;
                  background:#0e1116; color:#e6e9ef; font:16px ui-monospace,Consolas,monospace; letter-spacing:.14em; text-align:center; }
          button { margin-top:16px; width:100%; padding:13px; border:0; border-radius:10px; cursor:pointer;
                   background:#4c8dff; color:#08101f; font-weight:600; font-size:15px; }
          button:hover { background:#6ba0ff; }
          pre { background:#0e1116; border:1px solid #232935; border-radius:10px; padding:14px;
                white-space:pre-wrap; word-break:break-all; font:12px ui-monospace,Consolas,monospace; color:#a9d3ff; }
          .ok { color:#63d19b; } .err { color:#ff7b72; }
          .badge { display:inline-block; padding:4px 10px; border-radius:999px; font-size:12px;
                   background:#1d2431; color:#8b95a7; border:1px solid #2b3242; }
        </style>
        """;

    public static string Landing(AccessState state)
    {
        var status = state switch
        {
            AccessState.Active => "sesja aktywna",
            AccessState.Pairing => "parowanie otwarte",
            _ => "zablokowane"
        };

        return $$"""
            <!doctype html><html lang="pl"><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>AgentVirtualHand</title>{{Style}}
            <div class="card">
              <h1>AgentVirtualHand</h1>
              <p class="sub">{{Environment.MachineName}} &middot; <span class="badge">{{status}}</span></p>
              <p class="sub">Zdalny dostęp do tej maszyny. Aby się połączyć, wejdź na <code>/pair</code> z kodem
              wygenerowanym w aplikacji albo zeskanuj kod QR.</p>
            </div></html>
            """;
    }

    public static string PairForm(string? code)
    {
        var prefilled = System.Net.WebUtility.HtmlEncode(code ?? "");

        return $$"""
            <!doctype html><html lang="pl"><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Parowanie - AgentVirtualHand</title>{{Style}}
            <div class="card">
              <h1>Parowanie</h1>
              <p class="sub">{{Environment.MachineName}} &middot; jednorazowy kod, po uzyciu okno zamyka się samo</p>
              <label for="code">Kod parowania</label>
              <input id="code" value="{{prefilled}}" placeholder="XXXX-XXXX" autocomplete="off" spellcheck="false">
              <button id="go">Sparuj i pobierz token</button>
              <div id="out"></div>
            </div>
            <script>
              const out = document.getElementById('out');
              document.getElementById('go').onclick = async () => {
                out.innerHTML = '<p class="sub">Łączenie...</p>';
                try {
                  const res = await fetch('/api/pair', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ code: document.getElementById('code').value.trim().toUpperCase(), client: 'przeglądarka' })
                  });
                  const data = await res.json();
                  if (!res.ok) { out.innerHTML = '<p class="err">' + (data.error || 'Błąd parowania') + '</p>'; return; }
                  const base = location.origin;
                  out.innerHTML = '<p class="ok">Sparowano. Token ważny do ' + new Date(data.expiresAt).toLocaleTimeString() + '.</p>'
                    + '<label>Wklej to do Claude Code</label><pre id="blk">'
                    + 'Zdalny host: ' + base + '\nToken: ' + data.token
                    + '\nInstrukcja API: curl -H "Authorization: Bearer ' + data.token + '" ' + base + '/api/help</pre>';
                } catch (e) {
                  out.innerHTML = '<p class="err">' + e + '</p>';
                }
              };
            </script></html>
            """;
    }
}
