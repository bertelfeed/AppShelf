// The language switch remembers the reader's choice; the English page reads it before rendering (see its <head>).
document.querySelectorAll("a[data-lang]").forEach(function (link) {
  link.addEventListener("click", function () {
    try { localStorage.setItem("appshelf-lang", link.dataset.lang); } catch (e) { /* storage blocked: nothing to remember */ }
  });
});

// The promo plays muted on its own unless the reader prefers less motion; its controls are always there.
var promo = document.querySelector(".video video");
if (promo && window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
  promo.removeAttribute("autoplay");
  promo.pause();
}
