namespace AgentShell.Services;

/// <summary>
/// The page served on the local web port. It mirrors the desktop panel: submit a task,
/// watch the live status and thinking, read the answer, cancel or reset.
/// All dynamic text is written with textContent, so agent output can never inject markup.
/// </summary>
public static class WebChatPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Desktop AI Agent</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; height: 100vh; display: flex; flex-direction: column;
         background: #09111A; color: #E7EEF7;
         font: 15px/1.45 "Segoe UI", system-ui, sans-serif; }
  header { display: flex; align-items: center; justify-content: space-between; gap: 12px;
           padding: 12px 18px; background: #111925; border-bottom: 1px solid #1D2734; }
  .brand { font-weight: 600; font-size: 17px; }
  .status { display: flex; align-items: center; gap: 8px; color: #8FA6BE; font-size: 13px; }
  .dot { width: 9px; height: 9px; border-radius: 50%; background: #3E5164; }
  .dot.busy { background: #38C172; box-shadow: 0 0 8px #38C172; }
  main { flex: 1; overflow-y: auto; padding: 18px; display: flex; flex-direction: column; gap: 14px; }
  .turn { background: #131D2A; border: 1px solid #1D2734; border-radius: 16px; padding: 14px 16px; }
  .turn .who { font-size: 12px; letter-spacing: .04em; text-transform: uppercase; color: #8FA6BE; margin-bottom: 6px; }
  .turn.you { background: #16243A; }
  .turn .answer { white-space: pre-wrap; word-break: break-word; }
  .turn .meta { margin-top: 8px; font-size: 12px; color: #8FA6BE; }
  .turn .meta.error { color: #FF8A8A; }
  .turn .meta.cancelled { color: #FFC46B; }
  details { margin-top: 10px; }
  summary { cursor: pointer; color: #8FA6BE; font-size: 12px; }
  pre { margin: 8px 0 0; padding: 10px; background: #0D1622; border-radius: 10px;
        white-space: pre-wrap; word-break: break-word; font: 12px/1.5 Consolas, monospace; color: #B9CBDD; }
  .live { padding: 0 18px 12px; }
  .live-head { color: #7FD1A3; font-size: 13px; margin-bottom: 6px; }
  .hidden { display: none; }
  footer { padding: 12px 18px 18px; background: #111925; border-top: 1px solid #1D2734; }
  form { display: flex; gap: 10px; align-items: flex-end; }
  textarea { flex: 1; resize: none; min-height: 46px; max-height: 160px; padding: 12px 14px;
             background: #0D1622; color: #E7EEF7; border: 1px solid #24313F; border-radius: 12px;
             font: inherit; outline: none; }
  textarea:focus { border-color: #3D6EA8; }
  button { padding: 12px 16px; border: 0; border-radius: 12px; background: #2A6FDB; color: #fff;
           font: inherit; font-weight: 600; cursor: pointer; }
  button:disabled { opacity: .45; cursor: default; }
  button.ghost { background: #1D2734; color: #B9CBDD; font-weight: 500; }
  .notice { color: #FFC46B; font-size: 13px; padding: 10px 18px; }
</style>
</head>
<body>
<header>
  <div class="brand">Desktop AI Agent</div>
  <div class="status"><span id="dot" class="dot"></span><span id="status">подключение…</span></div>
</header>
<main id="turns"></main>
<section id="live" class="live hidden">
  <div class="live-head" id="liveStatus"></div>
  <details><summary>Ход мыслей</summary><pre id="liveThinking"></pre></details>
</section>
<footer>
  <form id="form">
    <textarea id="prompt" rows="1" placeholder="Что сделать? Enter — отправить, Shift+Enter — новая строка"></textarea>
    <button type="submit" id="send">Отправить</button>
    <button type="button" id="cancel" class="ghost">Стоп</button>
    <button type="button" id="reset" class="ghost">Сброс</button>
  </form>
  <div id="notice" class="notice hidden"></div>
</footer>
<script>
(function () {
  var params = new URLSearchParams(window.location.search);
  var token = params.get('token') || localStorage.getItem('agent-token') || '';
  if (params.get('token')) { localStorage.setItem('agent-token', params.get('token')); }

  var turnsEl = document.getElementById('turns');
  var liveEl = document.getElementById('live');
  var liveStatusEl = document.getElementById('liveStatus');
  var liveThinkingEl = document.getElementById('liveThinking');
  var dotEl = document.getElementById('dot');
  var statusEl = document.getElementById('status');
  var promptEl = document.getElementById('prompt');
  var sendEl = document.getElementById('send');
  var noticeEl = document.getElementById('notice');
  var lastSignature = '';
  var lastState = null;

  function showNotice(text) {
    noticeEl.textContent = text;
    noticeEl.classList.remove('hidden');
    setTimeout(function () { noticeEl.classList.add('hidden'); }, 6000);
  }

  function api(path, body) {
    return fetch(path, {
      method: body ? 'POST' : 'GET',
      headers: { 'Content-Type': 'application/json', 'X-Auth-Token': token },
      body: body ? JSON.stringify(body) : undefined
    }).then(function (response) {
      if (!response.ok) { return response.text().then(function (t) { throw new Error(t || response.status); }); }
      return response.json();
    });
  }

  function makeTurn(turn) {
    var wrap = document.createElement('div');

    var you = document.createElement('div');
    you.className = 'turn you';
    var youWho = document.createElement('div');
    youWho.className = 'who';
    youWho.textContent = 'Ты';
    var youText = document.createElement('div');
    youText.className = 'answer';
    youText.textContent = turn.prompt;
    you.appendChild(youWho);
    you.appendChild(youText);
    wrap.appendChild(you);

    var agent = document.createElement('div');
    agent.className = 'turn';
    var agentWho = document.createElement('div');
    agentWho.className = 'who';
    agentWho.textContent = 'Агент';
    agent.appendChild(agentWho);

    if (turn.answer) {
      var answer = document.createElement('div');
      answer.className = 'answer';
      answer.textContent = turn.answer;
      agent.appendChild(answer);
    }
    if (turn.error) {
      var error = document.createElement('div');
      error.className = 'meta error';
      error.textContent = 'Ошибка: ' + turn.error;
      agent.appendChild(error);
    }
    if (turn.state === 'cancelled') {
      var cancelled = document.createElement('div');
      cancelled.className = 'meta cancelled';
      cancelled.textContent = 'Остановлено';
      agent.appendChild(cancelled);
    }
    if (turn.waitingForUser) {
      var waiting = document.createElement('div');
      waiting.className = 'meta';
      waiting.textContent = 'Ждёт данные от тебя';
      agent.appendChild(waiting);
    }
    if (turn.thinking) {
      var details = document.createElement('details');
      var summary = document.createElement('summary');
      summary.textContent = 'Ход мыслей';
      var pre = document.createElement('pre');
      pre.textContent = turn.thinking;
      details.appendChild(summary);
      details.appendChild(pre);
      agent.appendChild(details);
    }

    wrap.appendChild(agent);
    return wrap;
  }

  function render(state) {
    var signature = JSON.stringify({ busy: state.busy, turns: state.turns.length, progress: state.progress });
    if (signature === lastSignature) { return; }
    lastSignature = signature;
    lastState = state;

    turnsEl.textContent = '';
    if (state.turns.length === 0) {
      var empty = document.createElement('div');
      empty.className = 'turn';
      empty.textContent = 'Напиши задачу — агент возьмёт управление мышью и клавиатурой.';
      turnsEl.appendChild(empty);
    }
    state.turns.forEach(function (turn) { turnsEl.appendChild(makeTurn(turn)); });

    if (state.busy && state.progress) {
      liveEl.classList.remove('hidden');
      liveStatusEl.textContent = state.progress.status || 'Работаю…';
      liveThinkingEl.textContent = state.progress.thinking || '';
    } else {
      liveEl.classList.add('hidden');
    }

    dotEl.classList.toggle('busy', !!state.busy);
    statusEl.textContent = state.busy ? 'работаю' : 'свободен';
    sendEl.disabled = !!state.busy;
    promptEl.disabled = !!state.busy;
    turnsEl.scrollTop = turnsEl.scrollHeight;
    window.scrollTo(0, document.body.scrollHeight);
  }

  document.getElementById('form').addEventListener('submit', function (event) {
    event.preventDefault();
    var prompt = promptEl.value.trim();
    if (!prompt) { return; }
    promptEl.value = '';
    api('/api/prompt', { prompt: prompt })
      .then(function () { return refresh(); })
      .catch(function (error) { showNotice('Не отправилось: ' + error.message); });
  });

  promptEl.addEventListener('keydown', function (event) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      document.getElementById('form').dispatchEvent(new Event('submit', { cancelable: true }));
    }
  });

  document.getElementById('cancel').addEventListener('click', function () {
    api('/api/cancel', {}).catch(function () {});
  });

  document.getElementById('reset').addEventListener('click', function () {
    api('/api/reset', {}).then(function () { lastSignature = ''; return refresh(); }).catch(function () {});
  });

  function refresh() {
    return api('/api/state').then(render).catch(function (error) {
      statusEl.textContent = 'ошибка связи';
      showNotice(String(error.message || error));
    });
  }

  if (!token) {
    showNotice('Нет токена. Открой страницу по ссылке с ?token=… из настроек приложения.');
  }

  refresh();
  var events = new EventSource('/api/events?token=' + encodeURIComponent(token));
  events.onmessage = function (message) {
    try { render(JSON.parse(message.data)); } catch (ignored) {}
  };
  events.onerror = function () {
    statusEl.textContent = 'переподключение…';
  };
})();
</script>
</body>
</html>
""";
}
