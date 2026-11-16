// Legacy front-end budgetService.generatePdff: the base64 answer decoded into a Blob and saved through a temporary
// link with the file name. The object URL is released a little later (legacy releases it at once, which some
// browsers cancel the download for).
export function downloadBase64(fileName, base64, contentType) {
  const text = atob(base64);
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) {
    bytes[i] = text.charCodeAt(i);
  }

  const link = document.createElement("a");
  link.download = fileName;
  link.href = URL.createObjectURL(new Blob([bytes], { type: contentType }));
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  setTimeout(() => URL.revokeObjectURL(link.href), 10000);
}
