// The one place that knows where the JWT lives. The API client, the login screen,
// sign-out and the route guard all go through these three functions, so moving
// the token to sessionStorage or memory later is a change here and nowhere else.
// localStorage can throw when a browser blocks site data; for a single-user app
// that's a fair "this browser can't run the app" failure, so it isn't caught.
const KEY = 'gymnotebook.token'

export function getToken(): string | null {
  return localStorage.getItem(KEY)
}

export function setToken(token: string): void {
  localStorage.setItem(KEY, token)
}

export function clearToken(): void {
  localStorage.removeItem(KEY)
}
