// The printable request page: the button opens the browser's print dialog (which can also save a PDF).
document.addEventListener('click', (e) => {
  if (e.target.closest && e.target.closest('[data-print]')) window.print();
});
