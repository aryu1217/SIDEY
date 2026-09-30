(function () {
  "use strict";

  const items = [...document.querySelectorAll("[data-release-item]")];
  const sentinel = document.querySelector("[data-release-sentinel]");
  const status = document.querySelector("[data-release-status]");
  const batchSize = 3;
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

  // GitHub language links can target older cards outside the first batch.
  function openLinkedRelease() {
    let id;
    try { id = decodeURIComponent(window.location.hash.slice(1)); } catch { return; }
    const target = items.find((item) => item.id === id);
    if (!target) return;
    items.slice(0, items.indexOf(target) + 1).forEach((item) => { item.hidden = false; });
    target.open = true;
    target.classList.add("is-expanded");
    target.scrollIntoView({ block: "start" });
  }
  openLinkedRelease();
  window.addEventListener("hashchange", openLinkedRelease);

  items.forEach((item) => {
    const summary = item.querySelector("summary");
    const answer = item.querySelector(".update-answer");
    if (!summary || !answer || reducedMotion.matches) return;
    item.classList.add("update-motion-ready");

    let closeTimer;
    summary.addEventListener("click", (event) => {
      event.preventDefault();
      window.clearTimeout(closeTimer);

      if (item.open) {
        item.classList.remove("is-expanded");
        closeTimer = window.setTimeout(() => {
          item.open = false;
        }, 280);
        return;
      }

      item.open = true;
      answer.getBoundingClientRect();
      window.requestAnimationFrame(() => item.classList.add("is-expanded"));
    });
  });

  if (!sentinel || items.every((item) => !item.hidden)) return;

  const revealNextBatch = () => {
    const hiddenItems = items.filter((item) => item.hidden).slice(0, batchSize);
    hiddenItems.forEach((item) => { item.hidden = false; });
    if (status && hiddenItems.length) status.textContent = status.dataset.loadedLabel ?? "";
    if (items.every((item) => !item.hidden)) {
      observer.disconnect();
      sentinel.remove();
    }
  };

  const observer = new IntersectionObserver((entries) => {
    if (entries.some((entry) => entry.isIntersecting)) revealNextBatch();
  }, { rootMargin: "320px 0px" });

  observer.observe(sentinel);
})();
