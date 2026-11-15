// PLAN.md §4.6, §8.2: the login token in localStorage["token"] (legacy: sessionStorage, which loses the login when
// the tab or the browser closes). The "storage" event tells every other tab of the same origin when it changes, so a
// logout (or a login) in one tab reaches all of them.
const key = "token";

export function getToken() {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null; // storage blocked (private mode, policy): behave as logged out
  }
}

export function setToken(token) {
  window.localStorage.setItem(key, token);
}

export function removeToken() {
  window.localStorage.removeItem(key);
}

// Calls dotnet.invokeMethodAsync("OnTokenChanged", tokenOrNull) when another tab changes or clears the token.
export function watchToken(dotnet) {
  const handler = (event) => {
    if (event.storageArea === window.localStorage && (event.key === key || event.key === null)) {
      dotnet.invokeMethodAsync("OnTokenChanged", getToken());
    }
  };
  window.addEventListener("storage", handler);
  return { dispose: () => window.removeEventListener("storage", handler) };
}
