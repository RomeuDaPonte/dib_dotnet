// Legacy _helpers/fallbackCopyToClipboard.js: navigator.clipboard when it exists, else a hidden textarea and
// execCommand("copy") (pages served over plain http on the LAN have no navigator.clipboard, ADR-0001).
export async function copyText(text) {
  if (navigator.clipboard && navigator.clipboard.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      return false;
    }
  }

  const area = document.createElement("textarea");
  area.value = text;
  document.body.appendChild(area);
  area.select();
  try {
    return document.execCommand("copy");
  } finally {
    document.body.removeChild(area);
  }
}
