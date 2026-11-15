"""Run against a disposable CineVault container; creates and modifies test users."""
import argparse
import http.cookiejar
import json
import re
import secrets
import urllib.error
import urllib.parse
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

class Tokens(HTMLParser):
    def __init__(self):
        super().__init__()
        self.token = None
    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "input" and a.get("name") == "__RequestVerificationToken":
            self.token = a.get("value")
        if tag == "meta" and a.get("name") == "csrf-token":
            self.token = a.get("content")

class Client:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), NoRedirect())
    def request(self, route, method="GET", form=None, data=None, token=None):
        headers = {}
        body = None
        if form is not None:
            body = urllib.parse.urlencode(form).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        if data is not None:
            body = json.dumps(data).encode()
            headers["Content-Type"] = "application/json"
        if token:
            headers["X-CSRF-TOKEN"] = token
        request = urllib.request.Request(self.base + route, data=body, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.code, response.read().decode("utf-8"), response.headers
    def token(self, route="/"):
        status, html, _ = self.request(route)
        assert status == 200, (route, status)
        parser = Tokens()
        parser.feed(html)
        assert parser.token, "missing antiforgery token"
        return parser.token
    def login(self, username, password, return_url="/"):
        token = self.token("/account/login")
        return self.request("/account/login", "POST", form={"UserName": username, "Password": password,
            "ReturnUrl": return_url, "__RequestVerificationToken": token})

parser = argparse.ArgumentParser()
parser.add_argument("--base-url", required=True)
parser.add_argument("--password-file", required=True)
parser.add_argument("--allow-test-users", action="store_true", required=True)
parser.add_argument("--persistence-check", action="store_true")
parser.add_argument("--cookie-file")
args = parser.parse_args()
password = Path(args.password_file).read_text(encoding="utf-8-sig").strip()
checks = 0
def check(condition, message):
    global checks
    if not condition:
        raise AssertionError(message)
    checks += 1
    print("PASS:", message)

admin = Client(args.base_url)
if args.persistence_check:
    if args.cookie_file:
        saved = http.cookiejar.LWPCookieJar(args.cookie_file)
        saved.load(ignore_discard=True, ignore_expires=True)
        for cookie in saved:
            admin.jar.set_cookie(cookie)
        check(admin.request("/api/settings")[0] == 200, "existing session survives container recreation with persisted encryption keys")
        admin = Client(args.base_url)
    check(admin.login("admin", password)[0] == 302, "administrator can log in after container recreation")
    check("smokeuser" in admin.request("/account/users")[1], "created local users survive container recreation")
    check(json.loads(admin.request("/api/settings")[1])["minRating"] == 7.5, "saved settings survive container recreation")
    print(f"All {checks} persistence checks passed.")
    raise SystemExit(0)

check(admin.request("/")[0] == 302, "anonymous workbench redirects to login")
check(admin.request("/api/media")[0] == 401, "anonymous API access returns 401")
check(admin.request("/app.js")[0] == 302, "application assets require authentication")
check(admin.request("/styles.css")[0] == 200, "login stylesheet is available")
check(admin.request("/healthz")[0] == 200, "minimal health probe works without credentials")
check(admin.request("/account/login", "POST", form={"UserName": "admin", "Password": password})[0] == 400, "login rejects missing antiforgery token")
check(admin.login("admin", "incorrect-password")[0] == 200, "wrong password does not authenticate")
status, _, headers = admin.login("admin", password, "https://example.invalid/")
check(status == 302 and headers.get("Location") == "/", "built-in admin login works and prevents open redirects")
root_html = admin.request("/")[1]
check("cinevault-server" in root_html and "用户管理" in root_html, "authenticated workbench includes server session controls")
session_cookie = next(c for c in admin.jar if c.name == "CineVault.Session")
cookie_options = {key.lower(): value for key, value in session_cookie._rest.items()}
check("httponly" in cookie_options and cookie_options.get("samesite", "").lower() == "strict", "session cookie uses HttpOnly and SameSite Strict")
token = admin.token()
status, text, _ = admin.request("/api/settings")
settings = json.loads(text)
check(status == 200 and settings["directory"] == "/downloads", "authenticated API uses container download directory")
check(admin.request("/api/settings", "PUT", data=settings)[0] == 400, "API mutation rejects missing antiforgery token")
settings["minRating"] = 7.5
check(admin.request("/api/settings", "PUT", data=settings, token=token)[0] == 200, "authenticated API mutation accepts valid token")
check(admin.request("/api/directory/check", "POST", data={"directory": "/downloads/Smoke"}, token=token)[0] == 200, "mapped download directory is writable")
check(admin.request("/api/directory/check", "POST", data={"directory": "/etc"}, token=token)[0] == 400, "directory checks reject paths outside the mounted root")

user_password = secrets.token_urlsafe(20)
new_password = secrets.token_urlsafe(20)
reset_password = secrets.token_urlsafe(20)
user_form = {"UserName": "smokeuser", "DisplayName": "Smoke <script>test</script>", "Password": user_password, "Role": "User", "__RequestVerificationToken": token}
check(admin.request("/account/users", "POST", form=user_form)[0] == 302, "administrator creates a built-in user")
users_html = admin.request("/account/users")[1]
check("Smoke &lt;script&gt;test&lt;/script&gt;" in users_html, "user names are HTML encoded")
card = next(c for c in re.findall(r'<article class="user-card">([\s\S]*?)</article>', users_html) if "smokeuser" in c)
user_id = re.search(r"/account/users/([a-f0-9]+)/password", card).group(1)
user = Client(args.base_url)
check(user.login("smokeuser", user_password)[0] == 302, "new user can log in")
check(user.request("/account/users")[0] == 403, "ordinary user cannot access administrator pages")
check(user.request("/account/users", "POST", form={"__RequestVerificationToken": user.token()})[0] == 403, "ordinary user cannot invoke account administration")
user_token = user.token("/account/password")
change = {"CurrentPassword": user_password, "NewPassword": new_password, "ConfirmPassword": new_password, "__RequestVerificationToken": user_token}
check(user.request("/account/password", "POST", form=change)[0] == 302, "user can change their own password")
check(user.request("/api/media")[0] == 401, "password change revokes the previous login")
check(user.login("smokeuser", user_password)[0] == 200 and user.login("smokeuser", new_password)[0] == 302, "old password stops working and new password succeeds")
check(admin.request("/account/users/"+user_id+"/password", "POST", form={"password":reset_password,"__RequestVerificationToken":token})[0] == 302, "administrator resets another user's password")
check(user.request("/api/media")[0] == 401, "password reset immediately revokes existing sessions")
check(user.login("smokeuser", reset_password)[0] == 302, "reset password authenticates")
check(admin.request("/account/users/"+user_id+"/status", "POST", form={"disabled":"true","__RequestVerificationToken":token})[0] == 302, "administrator disables a user")
check(user.request("/api/media")[0] == 401 and user.login("smokeuser", reset_password)[0] == 200, "disabled user loses active sessions and cannot sign in")
admin.request("/account/users/"+user_id+"/status", "POST", form={"disabled":"false","__RequestVerificationToken":token})
for _ in range(5):
    user.login("smokeuser", "invalid-test-password")
check(user.login("smokeuser", reset_password)[0] == 200, "five failed passwords lock out even the correct password")
admin.request("/account/users/"+user_id+"/password", "POST", form={"password":reset_password,"__RequestVerificationToken":token})
check(user.login("smokeuser", reset_password)[0] == 302, "administrator password reset clears account lockout")
check(user.request("/account/logout", "POST", form={})[0] == 400, "logout requires an antiforgery token")
check(user.request("/account/logout", "POST", form={"__RequestVerificationToken":user.token()})[0] == 302 and user.request("/api/media")[0] == 401, "logout clears the authenticated session")
if args.cookie_file:
    saved = http.cookiejar.LWPCookieJar(args.cookie_file)
    for cookie in admin.jar:
        saved.set_cookie(cookie)
    saved.save(ignore_discard=True, ignore_expires=True)
print(f"All {checks} authentication checks passed.")
