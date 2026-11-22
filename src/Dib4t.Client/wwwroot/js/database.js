// Settings → Base de Dados (legacy settings/databaseBackup.jsx + services/databaseService.js, PLAN.md §7.6).
// A backup can be 1 GB: neither the download nor the upload passes through WASM memory. The browser fetches the
// download into a Blob and saves it through a temporary link; the chosen File stays here and is sent with an
// XMLHttpRequest (fetch has no upload progress). .NET only gets the progress and the answer's status and text.

const tokenHeader = "x-auth-token";
const progressInterval = 100; // ms between progress callbacks (the last one is always sent)

let chosenFile = null;

// The <input type="file"> selection: { name, size, lastModified } or null. The input is cleared so that choosing the
// same file again fires "change" again (legacy).
export function takeFile(input) {
  const file = input && input.files && input.files[0];
  input.value = "";
  chosenFile = file || null;
  return file ? { name: file.name, size: file.size, lastModified: file.lastModified } : null;
}

export function forgetFile() {
  chosenFile = null;
}

export function chooseFile(input) {
  input.click();
}

function headers(token) {
  return token ? { [tokenHeader]: token } : {};
}

// GET the backup; on 2xx saves it as fileName. Returns { status, text, size }; status 0 = no answer or the download
// was cut off (the server aborts the connection when a backup fails half-way).
export async function downloadBackup(url, token, fileName, dotnet) {
  let response;
  try {
    response = await fetch(url, { headers: headers(token), cache: "no-store" });
  } catch {
    return { status: 0, text: "", size: 0 };
  }

  if (!response.ok) {
    let text = "";
    try {
      text = await response.text();
    } catch {
      // body cut off: no text
    }
    return { status: response.status, text, size: 0 };
  }

  const chunks = [];
  let loaded = 0;
  let last = 0;
  try {
    const reader = response.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      loaded += value.length;
      const now = Date.now();
      if (now - last >= progressInterval) {
        last = now;
        await dotnet.invokeMethodAsync("OnDownloadProgress", loaded);
      }
    }
  } catch {
    return { status: 0, text: "", size: 0 };
  }
  await dotnet.invokeMethodAsync("OnDownloadProgress", loaded);

  const blob = new Blob(chunks, { type: response.headers.get("content-type") || "application/gzip" });
  const link = document.createElement("a");
  link.href = URL.createObjectURL(blob);
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
  return { status: response.status, text: "", size: blob.size };
}

// POST the chosen file as application/octet-stream, with the page's id for this restore (the server's status names
// it, so a page that lost the connection can follow the restore to its end). Returns { status, text }; status 0 = no
// answer.
export function restoreBackup(url, token, restoreId, dotnet) {
  const file = chosenFile;
  chosenFile = null;
  return new Promise((resolve) => {
    if (!file) {
      resolve({ status: -1, text: "" });
      return;
    }

    const xhr = new XMLHttpRequest();
    let last = 0;
    xhr.open("POST", url);
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    if (token) xhr.setRequestHeader(tokenHeader, token);
    if (restoreId) xhr.setRequestHeader("x-restore-id", restoreId);
    xhr.upload.onprogress = (e) => {
      const now = Date.now();
      if (e.loaded >= e.total || now - last >= progressInterval) {
        last = now;
        dotnet.invokeMethodAsync("OnUploadProgress", e.loaded, e.lengthComputable ? e.total : 0);
      }
    };
    xhr.onload = () => resolve({ status: xhr.status, text: xhr.responseText || "" });
    xhr.onerror = () => resolve({ status: 0, text: "" });
    xhr.onabort = () => resolve({ status: 0, text: "" });
    xhr.send(file);
  });
}

// Legacy: while a backup or restore runs, leaving or reloading the page asks first.
let unloadHandler = null;

export function guardUnload(on) {
  if (on && !unloadHandler) {
    unloadHandler = (e) => {
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", unloadHandler);
  } else if (!on && unloadHandler) {
    window.removeEventListener("beforeunload", unloadHandler);
    unloadHandler = null;
  }
}
