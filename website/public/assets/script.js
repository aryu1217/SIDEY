(function () {
  "use strict";

  const copyButtons = document.querySelectorAll("[data-copy-target]");

  async function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return;
    }

    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.className = "copy-fallback-input";
    document.body.appendChild(textarea);
    textarea.select();

    const copied = document.execCommand("copy");
    textarea.remove();

    if (!copied) {
      throw new Error("Copy command failed");
    }
  }

  copyButtons.forEach((button) => {
    let resetTimer;

    button.addEventListener("click", async () => {
      const target = document.getElementById(button.dataset.copyTarget);
      const status = document.getElementById(button.dataset.copyStatus);

      if (!target) {
        return;
      }

      window.clearTimeout(resetTimer);
      button.dataset.copyState = "idle";
      if (status) {
        status.textContent = "";
      }

      try {
        await copyText(target.textContent.trim());
        button.dataset.copyState = "success";
        if (status) {
          status.textContent = button.dataset.copyAnnouncement;
        }
      } catch (_error) {
        button.dataset.copyState = "failure";
        if (status) {
          status.textContent = button.dataset.copyFailureAnnouncement;
        }
      }

      resetTimer = window.setTimeout(() => {
        button.dataset.copyState = "idle";
        if (status) {
          status.textContent = "";
        }
      }, 2000);
    });
  });

  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

  const supportedAppStoreRegions = new Set(["GB", "CA", "AU", "SG", "HK", "MO", "TW", "JP", "KR", "US"]);
  const browserLanguages = navigator.languages?.length ? navigator.languages : [navigator.language];
  const preferredAppStoreRegion = browserLanguages
    .flatMap((language) => String(language ?? "").replaceAll("_", "-").split("-").slice(1))
    .map((part) => part.toUpperCase())
    .find((part) => supportedAppStoreRegions.has(part));

  if (preferredAppStoreRegion) {
    const storefront = preferredAppStoreRegion.toLowerCase();
    const localizedAppStoreURL = (value) => {
      const url = new URL(value, window.location.href);
      url.pathname = url.pathname.replace(/^\/(?:[a-z]{2}\/)?app\//i, `/${storefront}/app/`);
      return url.href;
    };

    document.querySelectorAll("[data-app-store-link]").forEach((link) => {
      link.href = localizedAppStoreURL(link.href);
    });
    document.querySelectorAll("[data-macos-url]").forEach((link) => {
      link.dataset.macosUrl = localizedAppStoreURL(link.dataset.macosUrl);
    });
  }

  document.querySelectorAll(".faq-item").forEach((item) => {
    const summary = item.querySelector("summary");
    const answer = item.querySelector(".faq-answer");
    if (!summary || !answer || reducedMotion.matches) return;
    item.classList.add("faq-motion-ready");

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

  document.querySelectorAll("[data-download-selector]").forEach((selector) => {
    const primary = selector.querySelector("[data-auto-download]");
    const toggle = selector.querySelector("[data-download-toggle]");
    const menu = selector.querySelector("[data-download-menu]");
    const label = selector.querySelector("[data-download-label]");
    const logo = selector.querySelector("[data-download-logo]");
    if (!primary || !toggle || !menu || !label || !logo) return;
    let menuTimer;
    let manualSelection = false;

    const setMenu = (open) => {
      window.clearTimeout(menuTimer);
      toggle.setAttribute("aria-expanded", String(open));
      if (open) {
        menu.hidden = false;
        menu.dataset.state = "opening";
        menu.getBoundingClientRect();
        window.requestAnimationFrame(() => {
          if (toggle.getAttribute("aria-expanded") === "true") menu.dataset.state = "open";
        });
        return;
      }
      if (menu.hidden) return;
      menu.dataset.state = "closing";
      menuTimer = window.setTimeout(() => {
        menu.hidden = true;
        delete menu.dataset.state;
      }, reducedMotion.matches ? 0 : 160);
    };
    const setPlatform = (platform, architecture) => {
      const key = platform === "windows" ? "windows" : "macos";
      const url = platform === "windows"
        ? architecture === "arm64" ? primary.dataset.windowsArm64Url : architecture === "x64" ? primary.dataset.windowsUrl : undefined
        : platform === "macos" ? primary.dataset.macosUrl : undefined;
      const supported = Boolean(url);
      primary.href = supported ? url : "#download";
      label.textContent = supported
        ? primary.dataset[architecture === "arm64" ? "windowsArm64Label" : `${key}Label`]
        : primary.dataset.chooseLabel;
      logo.src = logo.dataset[`${key}Logo`];
      logo.classList.toggle("download-platform-logo-windows", platform === "windows");
      logo.classList.toggle("download-platform-logo-macos", platform !== "windows");
      logo.hidden = !supported;
      primary.dataset.detectedPlatform = supported ? platform : "unsupported";
      if (architecture) primary.dataset.detectedArchitecture = architecture;
      else delete primary.dataset.detectedArchitecture;
    };

    const platformSource = navigator.userAgentData?.platform ?? navigator.platform ?? navigator.userAgent;
    if (/win/i.test(platformSource)) {
      // Reduced user agents and browser process architecture cannot identify the Windows CPU.
      // Keep manual selection available while hints are unavailable, denied, or pending.
      setPlatform("windows");
      if (navigator.userAgentData?.getHighEntropyValues) {
        Promise.resolve().then(() => navigator.userAgentData.getHighEntropyValues(["architecture", "bitness"]))
          .then((hints) => {
            if (manualSelection) return;
            const architecture = hints.bitness === "64"
              ? hints.architecture === "arm" ? "arm64" : hints.architecture === "x86" ? "x64" : undefined
              : undefined;
            setPlatform("windows", architecture);
          })
          .catch(() => { if (!manualSelection) setPlatform("windows"); });
      }
    }
    else if (/mac/i.test(platformSource)) setPlatform("macos");
    else setPlatform("unsupported");

    menu.querySelectorAll("a").forEach((link) => link.addEventListener("click", () => {
      manualSelection = true;
      setPlatform(link.dataset.windowsArchitecture ? "windows" : "macos", link.dataset.windowsArchitecture);
      setMenu(false);
    }));
    toggle.addEventListener("click", () => setMenu(toggle.getAttribute("aria-expanded") !== "true"));
    primary.addEventListener("click", (event) => {
      if (primary.dataset.detectedPlatform === "unsupported") {
        event.preventDefault();
        setMenu(toggle.getAttribute("aria-expanded") !== "true");
      }
    });
    document.addEventListener("click", (event) => {
      if (!selector.contains(event.target)) setMenu(false);
    });
    selector.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        setMenu(false);
        toggle.focus();
      }
    });
  });

  document.querySelectorAll("[data-hero-track]").forEach((track) => {
    const movingActors = [...track.querySelectorAll("[data-hero-motion]")];
    const fixedObstacles = [...track.querySelectorAll("[data-hero-obstacle]")];

    if (!movingActors.length || reducedMotion.matches) {
      return;
    }

    const maximumSpeed = 22;
    const overlapMaximumSpeed = 30;
    const walkingAcceleration = 32;
    const overlapForwardAcceleration = 64;
    const walkingDamping = 0.82;
    const overlapDamping = 0.92;
    const inset = 8;
    let lastFrame;

    track.classList.add("hero-motion-ready");

    const states = movingActors.map((actor) => {
      const maxX = Math.max(0, track.clientWidth - actor.offsetWidth);
      const start = Number.parseFloat(actor.dataset.start ?? "0");
      const direction = Number.parseFloat(actor.dataset.direction ?? "1") < 0 ? -1 : 1;
      return {
        actor,
        direction,
        velocity: 0,
        x: Math.min(Math.max(0, track.clientWidth * start), maxX),
      };
    });
    const maxX = (state) => Math.max(0, track.clientWidth - state.actor.offsetWidth);
    const rangesOverlap = (left, width, obstacleLeft, obstacleWidth) => {
      const right = left + width - inset;
      const obstacleRight = obstacleLeft + obstacleWidth - inset;
      return right > obstacleLeft + inset && left + inset < obstacleRight;
    };
    const overlapsCharacter = (state, candidateX = state.x) => {
      const overlapsFixed = fixedObstacles.some((obstacle) => (
        rangesOverlap(candidateX, state.actor.offsetWidth, obstacle.offsetLeft, obstacle.offsetWidth)
      ));
      const overlapsMoving = states.some((other) => (
        other !== state
        && rangesOverlap(candidateX, state.actor.offsetWidth, other.x, other.actor.offsetWidth)
      ));
      return overlapsFixed || overlapsMoving;
    };
    const render = (state) => {
      state.actor.style.setProperty("--hero-x", `${state.x}px`);
      state.actor.classList.toggle("is-facing-left", state.direction < 0);
      state.actor.classList.toggle("is-sliding", overlapsCharacter(state));
    };

    states.forEach(render);

    const resizeObserver = new ResizeObserver(() => {
      states.forEach((state) => {
        state.x = Math.min(state.x, maxX(state));
      });
      states.forEach(render);
    });
    resizeObserver.observe(track);

    const step = (time) => {
      const delta = lastFrame ? Math.min((time - lastFrame) / 1000, 0.05) : 0;
      lastFrame = time;

      states.forEach((state) => {
        const sliding = overlapsCharacter(state);
        const acceleration = walkingAcceleration + (sliding ? overlapForwardAcceleration : 0);
        const damping = sliding ? overlapDamping : walkingDamping;
        const speedLimit = sliding ? overlapMaximumSpeed : maximumSpeed;
        state.velocity += state.direction * acceleration * delta;
        state.velocity *= Math.pow(damping, delta * 30);
        state.velocity = Math.min(Math.max(state.velocity, -speedLimit), speedLimit);
        let nextX = state.x + state.velocity * delta;
        const rightWall = maxX(state);

        if (nextX >= rightWall) {
          nextX = rightWall;
          state.direction = -1;
          state.velocity = -Math.abs(state.velocity);
        } else if (nextX <= 0) {
          nextX = 0;
          state.direction = 1;
          state.velocity = Math.abs(state.velocity);
        }

        state.x = nextX;
      });
      states.forEach(render);
      window.requestAnimationFrame(step);
    };

    window.requestAnimationFrame(step);
  });

  const sectionLinks = [...document.querySelectorAll("[data-nav-section]")];
  const observedSections = sectionLinks
    .map((link) => ({
      link,
      section: document.getElementById(link.dataset.navSection),
    }))
    .filter(({ section }) => section);

  if (observedSections.length) {
    let updateQueued = false;

    const updateSectionNavigation = () => {
      const readingLine = window.scrollY + Math.min(window.innerHeight * 0.38, 320);
      let current;

      observedSections.forEach((item) => {
        if (item.section.offsetTop <= readingLine) {
          current = item;
        }
      });

      const reachedPageEnd = window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 4;
      if (reachedPageEnd) {
        current = observedSections.at(-1);
      }

      sectionLinks.forEach((link) => link.removeAttribute("aria-current"));
      current?.link.setAttribute("aria-current", "location");
      updateQueued = false;
    };

    const queueSectionNavigationUpdate = () => {
      if (!updateQueued) {
        updateQueued = true;
        window.requestAnimationFrame(updateSectionNavigation);
      }
    };

    window.addEventListener("scroll", queueSectionNavigationUpdate, { passive: true });
    window.addEventListener("resize", queueSectionNavigationUpdate);
    window.addEventListener("hashchange", queueSectionNavigationUpdate);
    updateSectionNavigation();
  }
})();
