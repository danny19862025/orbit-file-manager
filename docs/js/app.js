import {
  ORBIT_THEMES,
  ANATOMY_HOTSPOTS,
  FOUR_VIEWS,
  SAMPLE_SEARCH_INDEX,
  DISK_SPACE_TREE,
  ICON_PACKS,
  SETTINGS_PAGES,
  KEYBOARD_SHORTCUTS,
  CLI_COMMANDS_WINDOWS,
  CLI_COMMANDS_LINUX,
  LINUX_PARITY_ROWS,
} from './data.js';

// ============================================================================
// 1. Toast Notification Helper
// ============================================================================
let toastTimer;
function showToast(msg) {
  const el = document.getElementById('toast');
  if (!el) return;
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), 2200);
}

// ============================================================================
// 2. Exact Orbit Intro Moon Animation (ported from MainWindow.Intro.cs)
// ============================================================================
function initOrbitLogoAnimation() {
  const moonFar = document.getElementById('orbit-moon-far');
  const moonNear = document.getElementById('orbit-moon-near');
  const heroStage = document.getElementById('hero-orb-stage');
  const replayBtn = document.getElementById('replay-intro-btn');
  if (!moonFar || !moonNear) return;

  const Cx = 128, Cy = 128, RingRx = 100, RingRy = 38, RingTilt = -24;
  const MoonRest = Math.PI * 0.78;
  const rad = (RingTilt * Math.PI) / 180;
  const cosA = Math.cos(rad), sinA = Math.sin(rad);

  function placeMoon(t) {
    const dx = RingRx * Math.cos(t);
    const dy = RingRy * Math.sin(t);
    const x = Cx + dx * cosA - dy * sinA;
    const y = Cy + dx * sinA + dy * cosA;
    const isFar = Math.sin(t) < 0;

    moonFar.setAttribute('cx', x.toFixed(2));
    moonFar.setAttribute('cy', y.toFixed(2));
    moonNear.setAttribute('cx', x.toFixed(2));
    moonNear.setAttribute('cy', y.toFixed(2));

    moonFar.style.opacity = isFar ? '1' : '0';
    moonNear.style.opacity = isFar ? '0' : '1';
  }

  let animId = null;
  function playLap() {
    if (animId) cancelAnimationFrame(animId);
    const start = performance.now();
    const delayMs = 120;
    const lapMs = 2000;

    function tick(now) {
      const p = Math.min(1, Math.max(0, (now - start - delayMs) / lapMs));
      const eased = p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2;
      placeMoon(MoonRest + eased * 2 * Math.PI);
      if (p < 1) {
        animId = requestAnimationFrame(tick);
      } else {
        placeMoon(MoonRest);
        animId = null;
      }
    }
    animId = requestAnimationFrame(tick);
  }

  placeMoon(MoonRest);
  playLap();

  heroStage?.addEventListener('click', () => {
    playLap();
    showToast('Replaying Orbit opening animation');
  });
  replayBtn?.addEventListener('click', (e) => {
    e.stopPropagation();
    playLap();
  });
}

// ============================================================================
// 3. Ambient Orbital Starfield Canvas
// ============================================================================
function initStarfieldCanvas() {
  const canvas = document.getElementById('orbit-canvas');
  if (!canvas) return;
  const ctx = canvas.getContext('2d');
  let w = 0, h = 0;
  const stars = [];

  function resize() {
    w = canvas.width = window.innerWidth;
    h = canvas.height = window.innerHeight;
  }
  resize();
  window.addEventListener('resize', resize);

  for (let i = 0; i < 68; i++) {
    stars.push({
      x: Math.random() * window.innerWidth,
      y: Math.random() * window.innerHeight,
      r: Math.random() * 1.6 + 0.4,
      vx: (Math.random() - 0.5) * 0.16,
      vy: (Math.random() - 0.5) * 0.16,
      a: Math.random() * 0.65 + 0.18,
    });
  }

  let angle = 0;
  function render() {
    ctx.clearRect(0, 0, w, h);
    const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#CBA6F7';
    const lavender = getComputedStyle(document.documentElement).getPropertyValue('--lavender').trim() || '#B4BEFE';

    // Soft rotating orbital ring in background
    ctx.save();
    ctx.translate(w * 0.5, h * 0.24);
    ctx.rotate(-0.28);
    ctx.strokeStyle = accent;
    ctx.globalAlpha = 0.08;
    ctx.lineWidth = 1.4;
    ctx.beginPath();
    ctx.ellipse(0, 0, Math.min(w * 0.44, 540), 120, 0, 0, Math.PI * 2);
    ctx.stroke();

    // Small orbiting ambient satellite
    const sx = Math.cos(angle) * Math.min(w * 0.44, 540);
    const sy = Math.sin(angle) * 120;
    ctx.globalAlpha = 0.45;
    ctx.fillStyle = lavender;
    ctx.beginPath();
    ctx.arc(sx, sy, 3.2, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    angle += 0.0035;

    for (const s of stars) {
      s.x = (s.x + s.vx + w) % w;
      s.y = (s.y + s.vy + h) % h;
      ctx.globalAlpha = s.a;
      ctx.fillStyle = lavender;
      ctx.beginPath();
      ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2);
      ctx.fill();
    }
    requestAnimationFrame(render);
  }
  render();
}

// ============================================================================
// 4. Live Theme Engine (all 15 Orbit themes + Mica translucency slider)
// ============================================================================
function initThemeEngine() {
  const dock = document.getElementById('theme-dock-dots');
  const swatchesGrid = document.getElementById('theme-swatches-grid');
  const micaSlider = document.getElementById('mica-slider');
  const micaValue = document.getElementById('mica-value');
  const activeThemeLabel = document.getElementById('active-theme-name');

  let currentTheme = ORBIT_THEMES[0].name;

  function applyTheme(theme) {
    currentTheme = theme.name;
    const root = document.documentElement;
    root.style.setProperty('--base', theme.base);
    root.style.setProperty('--mantle', theme.mantle);
    root.style.setProperty('--crust', theme.crust);
    root.style.setProperty('--surface0', theme.surface0);
    root.style.setProperty('--surface1', theme.surface1);
    root.style.setProperty('--surface2', theme.surface2);
    root.style.setProperty('--text', theme.text);
    root.style.setProperty('--subtext', theme.subtext);
    root.style.setProperty('--accent', theme.accent);
    root.style.setProperty('--lavender', theme.lavender);

    if (activeThemeLabel) activeThemeLabel.textContent = theme.name;

    document.querySelectorAll('[data-theme-name]').forEach((el) => {
      el.classList.toggle('active', el.dataset.themeName === theme.name);
    });
  }

  // Quick dock (6 signature themes)
  const quickNames = ['Catppuccin Mocha', 'Tokyo Night', 'Gruvbox Dark', 'Everforest', 'Rosé Pine', 'Catppuccin Latte'];
  if (dock) {
    quickNames.forEach((name) => {
      const t = ORBIT_THEMES.find((x) => x.name === name);
      if (!t) return;
      const btn = document.createElement('button');
      btn.className = 'theme-dot-btn' + (t.name === currentTheme ? ' active' : '');
      btn.dataset.themeName = t.name;
      btn.title = `${t.name} — ${t.desc}`;
      btn.style.background = `linear-gradient(135deg, ${t.base} 48%, ${t.accent} 52%)`;
      btn.addEventListener('click', () => {
        applyTheme(t);
        showToast(`Theme switched to ${t.name}`);
      });
      dock.appendChild(btn);
    });
  }

  // Full 15-theme studio grid
  if (swatchesGrid) {
    ORBIT_THEMES.forEach((t) => {
      const btn = document.createElement('button');
      btn.className = 'theme-card-btn' + (t.name === currentTheme ? ' active' : '');
      btn.dataset.themeName = t.name;
      btn.innerHTML = `
        <span class="swatch-circle" style="background: linear-gradient(135deg, ${t.base} 50%, ${t.accent} 50%)"></span>
        <span>
          <strong>${t.name}</strong>
          <small>${t.desc}</small>
        </span>
      `;
      btn.addEventListener('click', () => {
        applyTheme(t);
        showToast(`Applied ${t.name} live across the page`);
      });
      swatchesGrid.appendChild(btn);
    });
  }

  // Mica transparency slider (5%–90%)
  if (micaSlider) {
    micaSlider.addEventListener('input', () => {
      const pct = Number(micaSlider.value);
      if (micaValue) micaValue.textContent = `${pct}%`;
      const alpha = Math.max(0.18, 1 - pct / 100);
      document.documentElement.style.setProperty('--glass-alpha', alpha.toFixed(2));
      document.documentElement.style.setProperty('--glass-blur', `${Math.round(8 + pct * 0.25)}px`);
    });
  }
}

// ============================================================================
// 5. Scroll Progress, Reveal Observer & 3D Hero Tilt
// ============================================================================
function initScrollEffects() {
  const bar = document.getElementById('scroll-progress');
  const heroFrame = document.getElementById('hero-window-frame');

  window.addEventListener('scroll', () => {
    const scrollTop = window.scrollY;
    const docH = document.documentElement.scrollHeight - window.innerHeight;
    if (bar && docH > 0) bar.style.width = `${(scrollTop / docH) * 100}%`;
    if (heroFrame) {
      heroFrame.classList.toggle('revealed', scrollTop > 60);
    }
  }, { passive: true });

  // Subtle interactive 3D tilt on hero window
  if (heroFrame) {
    heroFrame.addEventListener('mousemove', (e) => {
      const rect = heroFrame.getBoundingClientRect();
      const nx = (e.clientX - rect.left) / rect.width - 0.5;
      const ny = (e.clientY - rect.top) / rect.height - 0.5;
      heroFrame.style.transform = `rotateX(${(-ny * 5).toFixed(2)}deg) rotateY(${(nx * 6).toFixed(2)}deg)`;
    });
    heroFrame.addEventListener('mouseleave', () => {
      heroFrame.style.transform = '';
    });
  }

  // Staggered reveal observer
  const io = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          io.unobserve(entry.target);
        }
      });
    },
    { threshold: 0.12 }
  );

  document.querySelectorAll('.reveal').forEach((el) => io.observe(el));
}

// ============================================================================
// 6. Interactive Main Window Anatomy Hotspots
// ============================================================================
function initAnatomyExplorer() {
  const stage = document.getElementById('hotspot-stage');
  const tabsWrap = document.getElementById('anatomy-tabs');
  const detailWrap = document.getElementById('anatomy-detail');
  if (!stage || !tabsWrap || !detailWrap) return;

  function selectHotspot(id) {
    const item = ANATOMY_HOTSPOTS.find((h) => h.id === id) || ANATOMY_HOTSPOTS[0];

    stage.querySelectorAll('.hotspot-pin').forEach((p) => {
      p.classList.toggle('active', p.dataset.id === item.id);
    });
    tabsWrap.querySelectorAll('.pill-tab').forEach((t) => {
      t.classList.toggle('active', t.dataset.id === item.id);
    });

    detailWrap.innerHTML = `
      <div class="anatomy-detail-title">
        <span class="kbd">#${item.num}</span> ${item.title}
      </div>
      <p class="anatomy-detail-desc">${item.desc}</p>
      <ul class="feature-bullet-list">
        ${item.bullets.map(([k, v]) => `<li><div><strong>${k}</strong> ${v}</div></li>`).join('')}
      </ul>
    `;
  }

  ANATOMY_HOTSPOTS.forEach((h) => {
    const pin = document.createElement('button');
    pin.className = 'hotspot-pin';
    pin.dataset.id = h.id;
    pin.style.left = `${h.x}%`;
    pin.style.top = `${h.y}%`;
    pin.textContent = h.num;
    pin.title = h.title;
    pin.addEventListener('click', () => selectHotspot(h.id));
    stage.appendChild(pin);

    const tab = document.createElement('button');
    tab.className = 'pill-tab';
    tab.dataset.id = h.id;
    tab.textContent = `${h.num}. ${h.title.split(' ')[0]}`;
    tab.addEventListener('click', () => selectHotspot(h.id));
    tabsWrap.appendChild(tab);
  });

  selectHotspot(ANATOMY_HOTSPOTS[0].id);
}

// ============================================================================
// 7. Interactive Four Views Switcher
// ============================================================================
function initFourViews() {
  const bar = document.getElementById('views-bar');
  const img = document.getElementById('view-showcase-img');
  const meta = document.getElementById('view-showcase-meta');
  if (!bar || !img || !meta) return;

  function selectView(id) {
    const v = FOUR_VIEWS.find((x) => x.id === id) || FOUR_VIEWS[0];
    bar.querySelectorAll('.view-card-btn').forEach((b) => {
      b.classList.toggle('active', b.dataset.id === v.id);
    });

    img.classList.add('switching');
    setTimeout(() => {
      img.src = v.img;
      img.alt = `Orbit ${v.name} view`;
      img.dataset.caption = `Orbit ${v.name} View (${v.shortcut}) — ${v.headline}`;
      img.classList.remove('switching');
    }, 160);

    meta.innerHTML = `
      <span class="kbd" style="margin-bottom: 12px;">${v.shortcut}</span>
      <h3>${v.name} View — ${v.headline}</h3>
      <p>${v.desc}</p>
      <div class="tag-chips">
        ${v.chips.map((c) => `<span class="chip">${c}</span>`).join('')}
      </div>
    `;
  }

  FOUR_VIEWS.forEach((v) => {
    const btn = document.createElement('button');
    btn.className = 'view-card-btn';
    btn.dataset.id = v.id;
    btn.innerHTML = `<span>${v.name}</span> <span class="kbd">${v.shortcut}</span>`;
    btn.addEventListener('click', () => selectView(v.id));
    bar.appendChild(btn);
  });

  selectView('grid');
}

// ============================================================================
// 8. Live Ctrl+K Fuzzy Search Simulator (modeled after FuzzySearch.cs)
// ============================================================================
function fuzzyMatch(text, query) {
  const q = query.trim().toLowerCase();
  if (!q) return { score: 1, html: text };
  const tokens = q.split(/\s+/).filter(Boolean);
  const lower = text.toLowerCase();
  const matchedIdx = new Set();
  let score = 0;

  for (const tok of tokens) {
    let ti = 0;
    let streak = 0;
    for (let i = 0; i < lower.length && ti < tok.length; i++) {
      if (lower[i] === tok[ti]) {
        matchedIdx.add(i);
        streak++;
        score += 10 + streak * 6 + (i === 0 || /[\s._\\-]/.test(lower[i - 1]) ? 12 : 0);
        ti++;
      } else {
        streak = 0;
      }
    }
    if (ti < tok.length) return null;
  }

  let html = '';
  for (let i = 0; i < text.length; i++) {
    html += matchedIdx.has(i) ? `<mark>${text[i]}</mark>` : text[i];
  }
  return { score, html };
}

function initFuzzySearchDemo() {
  const input = document.getElementById('fuzzy-input');
  const resultsEl = document.getElementById('fuzzy-results');
  const countEl = document.getElementById('fuzzy-count');
  if (!input || !resultsEl) return;

  const iconFor = {
    folder: '📁', pdf: '📄', app: '⚙️', code: '💻', lock: '🔒', archive: '📦', image: '🖼️', video: '🎬', audio: '🎵'
  };

  let activeIdx = 0;

  function renderResults() {
    const q = input.value;
    const hits = [];
    for (const item of SAMPLE_SEARCH_INDEX) {
      const m = fuzzyMatch(item.name, q);
      if (m) hits.push({ ...item, score: m.score, html: m.html });
    }
    hits.sort((a, b) => b.score - a.score);
    activeIdx = Math.min(activeIdx, Math.max(0, hits.length - 1));

    if (countEl) {
      countEl.textContent = q.trim()
        ? `${hits.length} matches from 184,290 indexed files (< 4 ms)`
        : `184,290 files indexed across C:\\ and D:\\`;
    }

    if (!hits.length) {
      resultsEl.innerHTML = `<div style="padding:24px;text-align:center;color:var(--subtext);font-size:0.88rem;">No matching files for "${q}". Try <code>invc 2026</code> or <code>lock</code>.</div>`;
      return;
    }

    resultsEl.innerHTML = hits.slice(0, 7).map((h, i) => `
      <div class="palette-row ${i === activeIdx ? 'selected' : ''}" data-idx="${i}">
        <div class="palette-row-left">
          <span>${iconFor[h.kind] || '📄'}</span>
          <div style="min-width:0">
            <div class="palette-name">${h.html}</div>
            <div class="palette-where">${h.where}</div>
          </div>
        </div>
        <span class="palette-score">${q.trim() ? `score ${h.score}` : h.kind}</span>
      </div>
    `).join('');

    resultsEl.querySelectorAll('.palette-row').forEach((row) => {
      row.addEventListener('click', () => {
        activeIdx = Number(row.dataset.idx);
        renderResults();
        showToast(`Opened ${hits[activeIdx].name} in Orbit`);
      });
    });
  }

  input.addEventListener('input', () => {
    activeIdx = 0;
    renderResults();
  });

  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      activeIdx++;
      renderResults();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      activeIdx = Math.max(0, activeIdx - 1);
      renderResults();
    }
  });

  document.querySelectorAll('.preset-chip').forEach((chip) => {
    chip.addEventListener('click', () => {
      input.value = chip.dataset.query;
      activeIdx = 0;
      renderResults();
      input.focus();
    });
  });

  renderResults();
}

// ============================================================================
// 9. Interactive Power Tools Suite
// ============================================================================
function initPowerTools() {
  const nav = document.getElementById('tools-nav');
  const stage = document.getElementById('tool-stage');
  if (!nav || !stage) return;

  const tools = {
    archives: {
      label: 'Archives & ISOs',
      render: () => `
        <div class="tool-grid">
          <div class="panel-img-frame">
            <img src="screenshots/archive.png" alt="Orbit Archives View" data-zoom data-caption="Browse ZIP, RAR, 7z and TAR archives as native arc: folders" />
          </div>
          <div>
            <span class="section-kicker">SharpCompress Powered</span>
            <h3 style="font-size:1.6rem;margin-bottom:10px;">Browse Archives Like Ordinary Folders</h3>
            <p style="color:var(--subtext);margin-bottom:16px;">Open <strong>ZIP, RAR, 7z, TAR</strong> (<code>.tar.gz</code>, <code>.tgz</code>, <code>.tar.bz2</code>, <code>.tar.xz</code>) and package formats (<code>.jar</code>, <code>.apk</code>, <code>.nupkg</code>, <code>.vsix</code>, <code>.whl</code>, <code>.cbz</code>) seamlessly under <code>arc:</code> paths.</p>
            <ul class="feature-bullet-list">
              <li><div><strong>Inspect & Preview:</strong> Sort, quick-preview, and open files inside archives without manually unpacking everything.</div></li>
              <li><div><strong>Drag Out & Extract:</strong> Drag individual files out, right-click for Extract to "name\\", Extract here, or Compress selected to ZIP.</div></li>
              <li><div><strong>ISO / VHD / VHDX Mounting:</strong> Mount disk images as Windows drives and Eject directly from the sidebar.</div></li>
              <li><div><strong>Safe & Transparent:</strong> Read-only inside Orbit; password-protected entries are reported clearly instead of failing silently.</div></li>
            </ul>
          </div>
        </div>
      `
    },
    space: {
      label: 'Disk Space Map',
      render: () => `
        <div class="tool-grid">
          <div>
            <div class="bubble-map-box">
              <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px;">
                <strong id="bubble-path" style="font-size:0.86rem;color:var(--accent);">Local Disk (C:)</strong>
                <button id="bubble-up-btn" class="btn btn-secondary btn-sm" style="display:none;">↑ Up</button>
              </div>
              <svg id="bubble-svg" class="bubble-svg" viewBox="0 0 460 270"></svg>
              <div class="bubble-footer">
                <span id="bubble-info">Click a bubble to inspect · Double-click folders to drill in</span>
                <span id="bubble-pct" style="font-family:monospace;color:var(--peach);font-weight:700;">412 / 512 GB</span>
              </div>
              <div class="drive-bar-track"><div id="bubble-bar" class="drive-bar-fill" style="width:80%"></div></div>
            </div>
          </div>
          <div>
            <span class="section-kicker">Interactive Space Analyzer</span>
            <h3 style="font-size:1.6rem;margin-bottom:10px;">Visual Bubble Disk Space Map</h3>
            <p style="color:var(--subtext);margin-bottom:14px;">Right-click any drive or open <code>⋯ › Analyze disk space…</code>. Orbit scans in the background and sizes bubbles proportionally to storage usage.</p>
            <div class="panel-img-frame" style="margin-bottom:14px;">
              <img src="screenshots/disk-space.png" alt="Orbit Disk Space Map" data-zoom data-caption="Orbit Disk Space Map — Read-only proportional bubble chart" />
            </div>
            <p style="font-size:0.84rem;color:var(--subtext);">100% <strong>read-only</strong> (never deletes anything). Click <em>Show in Orbit</em> to jump straight to any heavy item in the main window.</p>
          </div>
        </div>
      `,
      afterRender: initBubbleDemo
    },
    convert: {
      label: 'Media Converter',
      render: () => `
        <div class="tool-grid">
          <div>
            <div class="converter-demo">
              <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;">
                <strong style="font-size:0.9rem;">Live Size Estimator — <code>hero-photo.png</code> (8.4 MB)</strong>
                <span class="kbd">Never overwrites</span>
              </div>
              <div class="conv-format-grid" id="conv-formats">
                <button class="conv-card active" data-fmt="JPG" data-ratio="0.18"><strong>JPG</strong><small>Smallest for photos</small></button>
                <button class="conv-card" data-fmt="PNG" data-ratio="0.92"><strong>PNG</strong><small>Lossless + alpha</small></button>
                <button class="conv-card" data-fmt="TIFF" data-ratio="2.15"><strong>TIFF</strong><small>Print lossless</small></button>
                <button class="conv-card" data-fmt="BMP" data-ratio="2.80"><strong>BMP</strong><small>Uncompressed</small></button>
                <button class="conv-card" data-fmt="GIF" data-ratio="0.45"><strong>GIF</strong><small>256 colours</small></button>
                <button class="conv-card" data-fmt="MP4 / WebM" data-ratio="0.32"><strong>Video / Audio</strong><small>via ffmpeg</small></button>
              </div>
              <div style="margin-top:12px;">
                <label style="display:flex;justify-content:space-between;font-size:0.8rem;color:var(--subtext);margin-bottom:6px;">
                  <span>Quality / Max Dimension</span>
                  <span id="conv-q-label">Quality 85% · 1920px</span>
                </label>
                <input id="conv-q-slider" type="range" min="40" max="100" value="85" style="width:100%;accent-color:var(--accent);" />
              </div>
              <div class="conv-estimate-bar">
                <span id="conv-out-name">hero-photo (2).jpg</span>
                <strong id="conv-out-size" style="color:var(--green);">~1.51 MB (−82%)</strong>
              </div>
            </div>
          </div>
          <div>
            <span class="section-kicker">Zero-Overwrite Batch Converter</span>
            <h3 style="font-size:1.6rem;margin-bottom:10px;">Convert Pictures, Video & Audio</h3>
            <p style="color:var(--subtext);margin-bottom:14px;">Images use Windows native codecs with live output size estimates, optional max sizes (3840 / 1920 / 1280 px), and automatic white background fill for non-alpha formats. Video (MP4 H.264, WebM VP9, GIF, MP3) and Audio (MP3, M4A, OGG, WAV) auto-detect <code>ffmpeg</code>.</p>
            <div class="panel-img-frame">
              <img src="screenshots/convert.png" alt="Orbit Media Converter" data-zoom data-caption="Orbit Convert Window — Live size estimates for images, video, and audio" />
            </div>
          </div>
        </div>
      `,
      afterRender: initConverterDemo
    },
    lock: {
      label: 'Locked Folders (.orbitlock)',
      render: () => `
        <div class="tool-grid">
          <div class="panel-img-frame">
            <img src="screenshots/lock.png" alt="Orbit Locked Folders" data-zoom data-caption="Orbit Folder Lock — AES-256-GCM + PBKDF2-SHA256 (600,000 iterations)" />
          </div>
          <div>
            <span class="section-kicker">Authenticated Cryptography</span>
            <h3 style="font-size:1.6rem;margin-bottom:10px;">Password-Locked <code>.orbitlock</code> Vaults</h3>
            <p style="color:var(--subtext);margin-bottom:16px;">Right-click any folder › <strong>Lock with password…</strong> to seal it into a single tamper-evident file. Passwords are never stored and nothing touches disk unencrypted.</p>
            <div class="crypto-pipeline">
              <div class="crypto-step"><span class="crypto-badge">HEADER</span><span><code>ORBITLK1</code> magic + 16-byte random salt + 8-byte nonce prefix + chunk size</span></div>
              <div class="crypto-step"><span class="crypto-badge">KDF</span><span><strong>PBKDF2-SHA256</strong> with <strong>600,000 iterations</strong> derives the 256-bit key</span></div>
              <div class="crypto-step"><span class="crypto-badge">AEAD</span><span><strong>AES-256-GCM</strong> in 1 MB chunks with header+index AAD detects any tamper or truncation</span></div>
              <div class="crypto-step"><span class="crypto-badge">VERIFY</span><span>Full read-back verification before original folder moves safely to Recycle Bin</span></div>
            </div>
          </div>
        </div>
      `
    },
    recycle: {
      label: 'Recycle Bin & File Ops',
      render: () => `
        <div class="tool-grid">
          <div>
            <span class="section-kicker">Native Windows Integration</span>
            <h3 style="font-size:1.6rem;margin-bottom:10px;">Integrated Recycle Bin & Properties</h3>
            <p style="color:var(--subtext);margin-bottom:16px;">Browse Windows' Recycle Bin directly inside Orbit with original location & deletion date columns — always 100% in sync with File Explorer.</p>
            <ul class="feature-bullet-list">
              <li><div><strong>One-Click Restore:</strong> Restore individual items (recreating missing parent folders automatically), Restore all, Preview, or Empty Recycle Bin.</div></li>
              <li><div><strong>Drag-to-Delete:</strong> Drop files directly onto Recycle Bin in the sidebar, or press Delete / Shift+Delete.</div></li>
              <li><div><strong>Orbit Properties (Alt+Enter):</strong> Background folder size counting, item counts, Created/Modified/Accessed timestamps, Read-only & Hidden toggles, plus classic Windows Properties link.</div></li>
              <li><div><strong>Colour Tags & Dropzone:</strong> Tag files across folders, spring-loaded folder hover on drag, and hold Ctrl/Shift to copy/move.</div></li>
            </ul>
          </div>
          <div class="panel-img-frame">
            <img src="screenshots/main-grid.png" alt="Orbit File Operations & Sidebar" data-zoom data-caption="Orbit Sidebar Places, Recycle Bin, Colour Tags & Dropzone" />
          </div>
        </div>
      `
    }
  };

  function activateTool(key) {
    nav.querySelectorAll('.pill-tab').forEach((b) => b.classList.toggle('active', b.dataset.tool === key));
    const t = tools[key];
    stage.innerHTML = t.render();
    if (t.afterRender) t.afterRender();
    bindZoomImages(stage);
  }

  Object.entries(tools).forEach(([key, t]) => {
    const btn = document.createElement('button');
    btn.className = 'pill-tab';
    btn.dataset.tool = key;
    btn.textContent = t.label;
    btn.addEventListener('click', () => activateTool(key));
    nav.appendChild(btn);
  });

  activateTool('archives');
}

function initBubbleDemo() {
  const svg = document.getElementById('bubble-svg');
  const pathEl = document.getElementById('bubble-path');
  const upBtn = document.getElementById('bubble-up-btn');
  const infoEl = document.getElementById('bubble-info');
  const pctEl = document.getElementById('bubble-pct');
  const barEl = document.getElementById('bubble-bar');
  if (!svg) return;

  let stack = [DISK_SPACE_TREE];

  const positions = [
    { cx: 135, cy: 135, r: 78 },
    { cx: 285, cy: 112, r: 68 },
    { cx: 382, cy: 192, r: 50 },
    { cx: 245, cy: 215, r: 44 },
    { cx: 70, cy: 214, r: 40 },
  ];

  function draw() {
    const node = stack[stack.length - 1];
    pathEl.textContent = stack.map((s) => s.name).join(' › ');
    upBtn.style.display = stack.length > 1 ? 'inline-flex' : 'none';

    const items = node.children || [];
    svg.innerHTML = items.map((child, idx) => {
      const pos = positions[idx % positions.length];
      return `
        <g class="bubble-node" data-idx="${idx}">
          <circle cx="${pos.cx}" cy="${pos.cy}" r="${pos.r}" fill="${child.color}" fill-opacity="0.24" stroke="${child.color}" stroke-width="2.2" />
          <text x="${pos.cx}" y="${pos.cy - 4}" text-anchor="middle" fill="var(--text)" font-size="11.5" font-weight="700">${child.name}</text>
          <text x="${pos.cx}" y="${pos.cy + 14}" text-anchor="middle" fill="var(--subtext)" font-size="10.5" font-family="monospace">${child.sizeGB} GB${child.isDir ? ' ▸' : ''}</text>
        </g>
      `;
    }).join('');

    svg.querySelectorAll('.bubble-node').forEach((g) => {
      const child = items[Number(g.dataset.idx)];
      g.addEventListener('click', () => {
        const share = Math.round((child.sizeGB / node.sizeGB) * 100);
        infoEl.textContent = `${child.name} — ${child.sizeGB} GB (${share}% of ${node.name})`;
        pctEl.textContent = `${Math.round((child.sizeGB / DISK_SPACE_TREE.totalGB) * 100)}% of disk`;
        barEl.style.width = `${Math.min(100, Math.round((child.sizeGB / DISK_SPACE_TREE.totalGB) * 100))}%`;
      });
      g.addEventListener('dblclick', () => {
        if (child.children && child.children.length) {
          stack.push(child);
          draw();
        }
      });
    });
  }

  upBtn.onclick = () => {
    if (stack.length > 1) {
      stack.pop();
      draw();
    }
  };
  draw();
}

function initConverterDemo() {
  const wrap = document.getElementById('conv-formats');
  const slider = document.getElementById('conv-q-slider');
  const qLabel = document.getElementById('conv-q-label');
  const outName = document.getElementById('conv-out-name');
  const outSize = document.getElementById('conv-out-size');
  if (!wrap || !slider) return;

  let activeFmt = 'JPG';
  let activeRatio = 0.18;

  function update() {
    const q = Number(slider.value);
    const dim = q > 88 ? '3840px' : q > 65 ? '1920px' : '1280px';
    qLabel.textContent = `Quality ${q}% · Max ${dim}`;
    const factor = activeRatio * (0.55 + (q / 100) * 0.55);
    const estMB = Math.max(0.25, 8.4 * factor);
    const delta = Math.round(((estMB - 8.4) / 8.4) * 100);
    const ext = activeFmt.includes('Video') ? 'mp4' : activeFmt.toLowerCase();
    outName.textContent = `hero-photo (2).${ext}`;
    outSize.textContent = `~${estMB.toFixed(2)} MB (${delta > 0 ? '+' : ''}${delta}%)`;
    outSize.style.color = delta <= 0 ? 'var(--green)' : 'var(--peach)';
  }

  wrap.querySelectorAll('.conv-card').forEach((card) => {
    card.addEventListener('click', () => {
      wrap.querySelectorAll('.conv-card').forEach((c) => c.classList.remove('active'));
      card.classList.add('active');
      activeFmt = card.dataset.fmt;
      activeRatio = Number(card.dataset.ratio);
      update();
    });
  });

  slider.addEventListener('input', update);
  update();
}

// ============================================================================
// 10. Icon Packs Grid & Settings Carousel
// ============================================================================
function initIconPacksAndSettings() {
  const iconGrid = document.getElementById('icon-packs-grid');
  if (iconGrid) {
    iconGrid.innerHTML = ICON_PACKS.map((p) => `
      <div class="icon-pack-tile">
        <strong>${p.name}</strong>
        <span>${p.desc}</span>
      </div>
    `).join('');
  }

  const nav = document.getElementById('settings-nav');
  const img = document.getElementById('settings-preview-img');
  const title = document.getElementById('settings-preview-title');
  const desc = document.getElementById('settings-preview-desc');
  if (!nav || !img) return;

  function selectPage(id) {
    const p = SETTINGS_PAGES.find((x) => x.id === id) || SETTINGS_PAGES[0];
    nav.querySelectorAll('.settings-nav-btn').forEach((b) => b.classList.toggle('active', b.dataset.id === p.id));
    img.src = p.img;
    img.alt = `Orbit Settings — ${p.title}`;
    img.dataset.caption = `Settings › ${p.title} — ${p.sub}`;
    title.textContent = `Settings › ${p.title}`;
    desc.textContent = p.desc;
  }

  SETTINGS_PAGES.forEach((p) => {
    const btn = document.createElement('button');
    btn.className = 'settings-nav-btn';
    btn.dataset.id = p.id;
    btn.innerHTML = `<strong>${p.title}</strong><small>${p.sub}</small>`;
    btn.addEventListener('click', () => selectPage(p.id));
    nav.appendChild(btn);
  });

  selectPage('appearance');
}

// ============================================================================
// 11. Interactive Keyboard Shortcuts Explorer
// ============================================================================
function initShortcutsExplorer() {
  const catsWrap = document.getElementById('shortcut-cats');
  const searchInput = document.getElementById('shortcut-search');
  const grid = document.getElementById('shortcuts-grid');
  if (!catsWrap || !grid) return;

  const categories = ['All', 'General', 'Tabs', 'Navigation', 'View', 'Files', 'Fixed'];
  let activeCat = 'All';

  function render() {
    const q = (searchInput?.value || '').trim().toLowerCase();
    const filtered = KEYBOARD_SHORTCUTS.filter((s) => {
      const catOk = activeCat === 'All' || s.cat === activeCat;
      const qOk = !q || s.action.toLowerCase().includes(q) || s.keys.toLowerCase().includes(q);
      return catOk && qOk;
    });

    grid.innerHTML = filtered.map((s) => `
      <div class="shortcut-card">
        <div>
          <span class="shortcut-cat">${s.cat}</span>
          <span>${s.action}</span>
        </div>
        <span class="kbd">${s.keys}</span>
      </div>
    `).join('');
  }

  categories.forEach((cat) => {
    const btn = document.createElement('button');
    btn.className = 'pill-tab' + (cat === 'All' ? ' active' : '');
    btn.textContent = cat;
    btn.addEventListener('click', () => {
      activeCat = cat;
      catsWrap.querySelectorAll('.pill-tab').forEach((b) => b.classList.toggle('active', b.textContent === cat));
      render();
    });
    catsWrap.appendChild(btn);
  });

  searchInput?.addEventListener('input', render);
  render();
}

// ============================================================================
// 12. Hero Platform Toggle, Linux Parity Table & CLI Commands Simulator
// ============================================================================
function initPlatformAndLinux() {
  const heroImg = document.getElementById('hero-main-img');
  const toggleBtns = document.querySelectorAll('[data-hero-platform]');
  if (heroImg && toggleBtns.length) {
    const map = {
      linux: {
        src: 'screenshots/linux-grid.png',
        alt: 'Orbit File Manager on Linux (Avalonia)',
        caption: 'Orbit for Linux (Avalonia) — KDE Plasma, GNOME, Hyprland & other desktops',
      },
      windows: {
        src: 'screenshots/main-grid.png',
        alt: 'Orbit File Manager on Windows 11 (WPF)',
        caption: 'Orbit for Windows 10 & 11 (WPF) — Catppuccin Mocha, Material Icons',
      },
    };
    toggleBtns.forEach((btn) => {
      btn.addEventListener('click', () => {
        const key = btn.dataset.heroPlatform;
        const cfg = map[key];
        if (!cfg) return;
        toggleBtns.forEach((b) => b.classList.toggle('active', b === btn));
        heroImg.src = cfg.src;
        heroImg.alt = cfg.alt;
        heroImg.dataset.caption = cfg.caption;
      });
    });
  }

  const installSnippet = document.getElementById('linux-install-snippet');
  installSnippet?.addEventListener('click', () => {
    const cmd = 'tar xf Orbit-1.0.0-linux-x64.tar.gz && cd Orbit-1.0.0-linux-x64 && ./install.sh --default';
    navigator.clipboard?.writeText(cmd).catch(() => {});
    showToast('Copied Linux install commands');
  });

  const parityBody = document.getElementById('linux-parity-body');
  if (parityBody) {
    parityBody.innerHTML = LINUX_PARITY_ROWS.map(([area, win, lin]) => `
      <tr>
        <td>${area}</td>
        <td>${win}</td>
        <td>${lin}</td>
      </tr>
    `).join('');
  }
}

function initCliCommands() {
  const list = document.getElementById('cli-list');
  const tabs = document.querySelectorAll('[data-cli-os]');
  const titleEl = document.getElementById('cli-terminal-title');
  if (!list) return;

  function renderCli(os) {
    const items = os === 'linux' ? CLI_COMMANDS_LINUX : CLI_COMMANDS_WINDOWS;
    if (titleEl) {
      titleEl.textContent = os === 'linux'
        ? 'Linux Shell (bash / zsh) — Click any command to copy'
        : 'Windows PowerShell — Click any command to copy';
    }
    tabs.forEach((t) => t.classList.toggle('active', t.dataset.cliOs === os));
    list.innerHTML = items.map((c) => `
      <div class="cli-row" data-cmd="${c.cmd}" title="Click to copy command">
        <code class="cli-cmd">${c.cmd}</code>
        <span class="cli-desc">${c.desc}</span>
      </div>
    `).join('');

    list.querySelectorAll('.cli-row').forEach((row) => {
      row.addEventListener('click', () => {
        const cmd = row.dataset.cmd;
        navigator.clipboard?.writeText(cmd).catch(() => {});
        showToast(`Copied: ${cmd}`);
      });
    });
  }

  tabs.forEach((t) => t.addEventListener('click', () => renderCli(t.dataset.cliOs)));
  renderCli('linux');
}

// ============================================================================
// 13. Fullscreen Screenshot Lightbox
// ============================================================================
function bindZoomImages(scope = document) {
  const modal = document.getElementById('lightbox');
  const modalImg = document.getElementById('lightbox-img');
  const modalCap = document.getElementById('lightbox-caption');
  if (!modal || !modalImg) return;

  scope.querySelectorAll('img[data-zoom]').forEach((img) => {
    if (img.dataset.zoomBound) return;
    img.dataset.zoomBound = '1';
    img.addEventListener('click', () => {
      modalImg.src = img.src;
      modalImg.alt = img.alt;
      modalCap.textContent = img.dataset.caption || img.alt;
      modal.classList.add('open');
    });
  });
}

function initLightbox() {
  const modal = document.getElementById('lightbox');
  const closeBtn = document.getElementById('lightbox-close');
  if (!modal) return;

  const close = () => modal.classList.remove('open');
  closeBtn?.addEventListener('click', close);
  modal.addEventListener('click', (e) => { if (e.target === modal) close(); });
  window.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
  bindZoomImages(document);
}

// ============================================================================
// Boot
// ============================================================================
document.addEventListener('DOMContentLoaded', () => {
  initOrbitLogoAnimation();
  initStarfieldCanvas();
  initThemeEngine();
  initScrollEffects();
  initAnatomyExplorer();
  initFourViews();
  initFuzzySearchDemo();
  initPowerTools();
  initIconPacksAndSettings();
  initShortcutsExplorer();
  initPlatformAndLinux();
  initCliCommands();
  initLightbox();
});
