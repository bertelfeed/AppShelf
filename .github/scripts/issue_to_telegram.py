#!/usr/bin/env python3
"""Send a short Telegram message about a new or reopened GitHub Issue.

Used by .github/workflows/issues-telegram.yml. Standard library only.

The event is read from $GITHUB_EVENT_PATH, never from workflow expressions,
so issue text cannot reach a shell. The issue body is never sent.

  python3 issue_to_telegram.py              send (in GitHub Actions)
  python3 issue_to_telegram.py --self-test  check formatting and retries, send nothing
"""

import html
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

API_URL = "https://api.telegram.org/bot{token}/sendMessage"
TIMEOUT_SECONDS = 15
MAX_RETRY_AFTER = 60
MAX_TITLE = 200
MAX_LABELS = 10
MAX_LABEL = 50
MAX_LOGIN = 40


def shorten(text, limit):
    text = " ".join(str(text or "").split())
    return text if len(text) <= limit else text[: limit - 1].rstrip() + "…"


def esc(text):
    # Telegram HTML needs only &, < and >; quote=True also makes it safe inside href="...".
    return html.escape(text, quote=True)


def build_issue_message(event):
    issue = event.get("issue") or {}
    repo = (event.get("repository") or {}).get("full_name", "")
    action = event.get("action", "")
    headline = {"opened": "🆕 Новый Issue", "reopened": "🔁 Issue открыт снова"}.get(action, "Issue: " + action)

    title = shorten(issue.get("title"), MAX_TITLE) or "(без названия)"
    login = shorten((issue.get("user") or {}).get("login"), MAX_LOGIN) or "?"
    names = [shorten(label.get("name"), MAX_LABEL) for label in issue.get("labels") or [] if isinstance(label, dict)]
    labels = ", ".join(esc(name) for name in names[:MAX_LABELS] if name) or "нет"
    if len(names) > MAX_LABELS:
        labels += f" и ещё {len(names) - MAX_LABELS}"

    url = str(issue.get("html_url") or "")
    lines = [
        f"<b>{esc(headline)}</b> · {esc(repo)}",
        f"<b>#{int(issue.get('number') or 0)}</b> {esc(title)}",
        f"Метки: {labels}",
        f"Автор: {esc(login)}",
    ]
    if url.startswith("https://"):
        lines.append(f'<a href="{esc(url)}">Открыть на GitHub</a>')
    return "\n".join(lines)


def build_test_message(env):
    repo = env.get("GITHUB_REPOSITORY", "")
    run_url = f"{env.get('GITHUB_SERVER_URL', 'https://github.com')}/{repo}/actions/runs/{env.get('GITHUB_RUN_ID', '')}"
    return "\n".join([
        f"<b>🧪 ТЕСТ · {esc(repo)}</b>",
        "Проверка уведомлений об Issues. Настройка работает, ничего делать не нужно.",
        f"Запустил: {esc(shorten(env.get('GITHUB_ACTOR'), MAX_LOGIN))}",
        f'<a href="{esc(run_url)}">Запуск в Actions</a>',
    ])


def retry_delay(body, headers):
    try:
        value = json.loads(body).get("parameters", {}).get("retry_after")
    except (ValueError, AttributeError):
        value = None
    if value is None:
        value = (headers or {}).get("Retry-After")
    try:
        return max(1, min(int(value), MAX_RETRY_AFTER))
    except (TypeError, ValueError):
        return 5


def send(token, chat_id, text, opener=urllib.request.urlopen, sleep=time.sleep):
    """Return True on success. Retries once on 429, 5xx or a network error."""
    data = urllib.parse.urlencode({
        "chat_id": chat_id,
        "text": text,
        "parse_mode": "HTML",
        "link_preview_options": json.dumps({"is_disabled": True}),
    }).encode()
    request = urllib.request.Request(API_URL.format(token=token), data=data, method="POST")

    for attempt in (1, 2):
        try:
            with opener(request, timeout=TIMEOUT_SECONDS) as response:
                body = response.read().decode("utf-8", "replace")
            if json.loads(body).get("ok"):
                print("Telegram: message sent.")
                return True
            print("::error::Telegram answered without ok=true.")
            return False
        except urllib.error.HTTPError as error:
            body = error.read().decode("utf-8", "replace")
            try:
                description = json.loads(body).get("description", "")
            except ValueError:
                description = ""
            # Telegram's description never contains the token; the request URL does, so it is never printed.
            print(f"::warning::Telegram HTTP {error.code}: {description}")
            if attempt == 1 and (error.code == 429 or error.code >= 500):
                delay = retry_delay(body, error.headers)
                print(f"Retrying in {delay} s.")
                sleep(delay)
                continue
            break
        except (urllib.error.URLError, TimeoutError, OSError) as error:
            reason = getattr(error, "reason", error)
            print(f"::warning::Network error: {type(error).__name__}: {str(reason).replace(token, '***')}")
            if attempt == 1:
                sleep(5)
                continue
            break
    print("::error::Could not send the Telegram message.")
    return False


def main(env=os.environ):
    token = env.get("TELEGRAM_BOT_TOKEN", "").strip()
    chat_id = env.get("TELEGRAM_CHAT_ID", "").strip()
    if not token or not chat_id:
        print("::notice::TELEGRAM_BOT_TOKEN or TELEGRAM_CHAT_ID is not set. Nothing sent. See docs/FEEDBACK_AUTOMATION.md.")
        return 0

    if env.get("GITHUB_EVENT_NAME") == "workflow_dispatch":
        text = build_test_message(env)
    else:
        with open(env["GITHUB_EVENT_PATH"], encoding="utf-8") as file:
            text = build_issue_message(json.load(file))
    return 0 if send(token, chat_id, text) else 1


# ---------------------------------------------------------------- self-test

def self_test():
    from html.parser import HTMLParser
    import io

    class TagCheck(HTMLParser):
        def __init__(self):
            super().__init__()
            self.tags = []

        def handle_starttag(self, tag, attrs):
            self.tags.append(tag)

    def check_html(message):
        # Only our own <b> and <a> may reach Telegram as markup.
        parser = TagCheck()
        parser.feed(message)
        assert set(parser.tags) <= {"b", "a"}, f"unexpected tags {parser.tags}"
        assert len(message) <= 4096

    secret_body = "PRIVATE BODY: my email is someone@example.com"
    tricky = {
        "action": "opened",
        "repository": {"full_name": "bertelfeed/AppShelf"},
        "issue": {
            "number": 42,
            "title": "Папка <b>не</b> открывается & падает 🚀 <script>alert(1)</script>",
            "body": secret_body,
            "html_url": "https://github.com/bertelfeed/AppShelf/issues/42?a=1&b=\"2\"",
            "user": {"login": "user<i>"},
            "labels": [{"name": "bug"}, {"name": "<u>needs&triage</u>"}],
        },
    }
    long_title = dict(tricky, action="reopened", issue=dict(tricky["issue"], title="Я" * 499 + "&", labels=[{"name": f"l{i}"} for i in range(15)]))
    no_labels = {"action": "opened", "repository": {"full_name": "o/r"}, "issue": {"number": 1, "title": "", "user": {}, "labels": [], "html_url": "javascript:alert(1)"}}
    test_env = {"GITHUB_REPOSITORY": "bertelfeed/AppShelf", "GITHUB_RUN_ID": "123", "GITHUB_ACTOR": "owner<&>"}

    cases = [
        ("opened, HTML, & and emoji", build_issue_message(tricky)),
        ("reopened, 500-character title, 15 labels", build_issue_message(long_title)),
        ("no title, no labels, bad link", build_issue_message(no_labels)),
        ("workflow_dispatch test", build_test_message(test_env)),
    ]
    for name, message in cases:
        print(f"--- {name} ({len(message)} chars)\n{message}\n")
        check_html(message)
        assert secret_body not in message and "someone@" not in message, "issue body leaked"
        assert "<script>" not in message and "<i>" not in message and "<u>" not in message

    m0, m1, m2, m3 = (message for _, message in cases)
    assert "Папка &lt;b&gt;не&lt;/b&gt; открывается &amp; падает 🚀" in m0
    assert "&lt;u&gt;needs&amp;triage&lt;/u&gt;" in m0 and "user&lt;i&gt;" in m0
    assert 'href="https://github.com/bertelfeed/AppShelf/issues/42?a=1&amp;b=&quot;2&quot;"' in m0
    title_line = m1.splitlines()[1]
    assert title_line.endswith("…") and len(title_line) < MAX_TITLE + 20 and "Я" * 499 not in m1
    assert "🔁" in m1 and "и ещё 5" in m1 and "l9" in m1 and "l10" not in m1
    assert "(без названия)" in m2 and "Метки: нет" in m2 and "javascript:" not in m2
    assert "ТЕСТ" in m3 and "owner&lt;&amp;&gt;" in m3 and "/actions/runs/123" in m3

    # Missing secrets: success, nothing sent.
    out = io.StringIO()
    stdout, sys.stdout = sys.stdout, out
    try:
        assert main({"TELEGRAM_BOT_TOKEN": "", "TELEGRAM_CHAT_ID": "1"}) == 0
        assert main({"TELEGRAM_BOT_TOKEN": "x"}) == 0
    finally:
        sys.stdout = stdout
    assert out.getvalue().count("::notice::") == 2

    # Sending, with a fake network: success, retry on 429 with retry_after, give up on 400, never print the token.
    token = "123456:SECRET-TOKEN"

    class FakeResponse(io.BytesIO):
        def __enter__(self):
            return self

        def __exit__(self, *args):
            self.close()

    def fake_opener(script):
        calls = []

        def opener(request, timeout):
            calls.append(urllib.parse.parse_qs(request.data.decode()))
            assert timeout == TIMEOUT_SECONDS
            code, body = script[len(calls) - 1]
            if code == 200:
                return FakeResponse(body.encode())
            raise urllib.error.HTTPError(request.full_url, code, "error", {}, io.BytesIO(body.encode()))
        return opener, calls

    def run(script):
        opener, calls = fake_opener(script)
        sleeps, out = [], io.StringIO()
        stdout, sys.stdout = sys.stdout, out
        try:
            ok = send(token, "-100123", m0, opener=opener, sleep=sleeps.append)
        finally:
            sys.stdout = stdout
        assert token not in out.getvalue() and "SECRET" not in out.getvalue()
        return ok, calls, sleeps

    ok, calls, sleeps = run([(200, '{"ok":true}')])
    assert ok and len(calls) == 1 and not sleeps
    assert calls[0]["text"] == [m0] and calls[0]["parse_mode"] == ["HTML"] and calls[0]["chat_id"] == ["-100123"]
    ok, calls, sleeps = run([(429, '{"ok":false,"description":"Too Many Requests","parameters":{"retry_after":7}}'), (200, '{"ok":true}')])
    assert ok and len(calls) == 2 and sleeps == [7]
    ok, calls, sleeps = run([(502, "Bad Gateway"), (503, "")])
    assert not ok and len(calls) == 2 and sleeps == [5]
    ok, calls, sleeps = run([(400, '{"ok":false,"description":"Bad Request: chat not found"}')])
    assert not ok and len(calls) == 1 and not sleeps
    ok, calls, sleeps = run([(429, '{"parameters":{"retry_after":3600}}'), (200, '{"ok":true}')])
    assert ok and sleeps == [MAX_RETRY_AFTER]

    print("Self-test passed.")
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(self_test() if "--self-test" in sys.argv[1:] else main())
