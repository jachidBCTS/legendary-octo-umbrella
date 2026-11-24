// Greenlight easter egg (queue #12): clicking the "t" at the end of the logo plays a ~12 s pixel-art scene
// along the bottom of the page. He walks out from behind the menu, waves ("I'm out!"), his sunglasses drop on,
// he hops into the convertible, looks at you, waves, revs and drives off in a puff of smoke.
// Sprites load only on the first click; skipped when the person prefers reduced motion; Escape stops it.
// Everything is in "sprite pixels" (he is 300 tall, the car 760 wide) and the scene is scaled to fit.
(() => {
  'use strict';
  const { h } = window.SF;
  const SRC = '/img/egg/';
  const F = { // width, height, anchor x (middle of his body); bottoms line up at his feet
    f1: [158, 295, 68], f2: [160, 295, 76], f3: [168, 297, 69], fwave: [163, 300, 66],
    wave: [159, 300, 64], 'wave-g': [159, 300, 64],
    's1-g': [159, 300, 71], 's2-g': [157, 300, 70], 's3-g': [158, 300, 69],
    'h1-g': [121, 300, 47], 'h2-g': [106, 287, 42], 'h3-g': [122, 277, 38],
  };
  const CAR = { w: 760, h: 255, door: 181, seat: 318, wheels: [[145, 192], [580, 194]], R: 46, exhaust: [18, 62] };
  let stage = null;
  let running = null;
  let preloaded = false;

  const preload = () => {
    if (preloaded) return;
    preloaded = true;
    [...Object.keys(F), 'shades', 'car', 'wheel0', 'wheel1'].forEach((n) => { const i = new Image(); i.src = SRC + n + '.png'; });
  };
  const img = (name, cls) => h('img', { src: SRC + name + '.png', alt: '', class: cls || null });
  const ease = (t) => 1 - Math.pow(1 - t, 3);
  const lerp = (a, b, t) => a + (b - a) * t;

  function play() {
    if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    if (running) running.stop();
    preload();
    if (!stage) { stage = h('div', { class: 'egg-stage', 'aria-hidden': 'true' }); document.body.appendChild(stage); }
    const vw = stage.clientWidth || window.innerWidth;
    const k = Math.min(0.5, Math.max(0.27, vw / 1500));       // screen px per sprite px
    const W = vw / k;                                          // stage width in sprite px
    const scene = h('div', { class: 'egg-scene' });
    scene.style.width = W + 'px'; scene.style.height = '1000px'; scene.style.transform = 'scale(' + k + ')';
    stage.replaceChildren(scene);
    const G = 40;                                              // ground line above the screen's bottom

    const waveX = Math.max(240, W * 0.2);
    const carX = waveX + 300;                                  // parked just beside where he stops

    const car = img('car');
    const wheels = CAR.wheels.map((_, i) => img('wheel' + i, 'egg-wheel'));
    const man = img('f1');
    const shades = img('shades'); shades.hidden = true;
    const bubble = h('div', { class: 'egg-bubble', hidden: true }, "I'm out!");
    scene.append(car, man, ...wheels, shades, bubble);

    const st = { frame: 'f1', x: -140, y: 0, rot: 0, clip: 0, carX, carY: 0, spin: 0 };
    function draw() {
      const [w, ht, ax] = F[st.frame];
      if (man.dataset.f !== st.frame) { man.src = SRC + st.frame + '.png'; man.dataset.f = st.frame; }
      man.style.left = (st.x - ax) + 'px'; man.style.bottom = (G + st.y) + 'px';
      man.style.width = w + 'px'; man.style.height = ht + 'px';
      man.style.transformOrigin = ax + 'px 100%'; man.style.transform = 'rotate(' + st.rot + 'deg)';
      man.style.clipPath = st.clip > 0 ? 'inset(0 0 ' + st.clip + 'px 0)' : 'none';
      car.style.left = st.carX + 'px'; car.style.bottom = (G + st.carY) + 'px';
      wheels.forEach((wh, i) => {
        const [cx, cy] = CAR.wheels[i];
        wh.style.left = (st.carX + cx - CAR.R) + 'px'; wh.style.bottom = (G + st.carY + CAR.h - cy - CAR.R) + 'px';
        wh.style.transform = 'rotate(' + st.spin + 'deg)';
      });
    }
    // Inside the car, everything of him below the top of the door is hidden.
    const doorClip = () => { st.clip = Math.max(0, CAR.door + st.carY - st.y); };

    function puff(strong) {
      const p = h('div', { class: 'egg-puff' });
      p.style.left = (st.carX + CAR.exhaust[0] - 50) + 'px'; p.style.bottom = (G + st.carY + CAR.exhaust[1]) + 'px';
      p.style.setProperty('--dx', -(80 + Math.random() * (strong ? 160 : 70)) + 'px');
      p.style.setProperty('--dy', -(30 + Math.random() * 70) + 'px');
      scene.append(p); setTimeout(() => p.remove(), 1000);
    }
    function glint() {
      const g = h('div', { class: 'egg-glint' });
      g.style.left = (st.x - 64 + 52) + 'px'; g.style.bottom = (G + st.y + 300 - 33) + 'px';
      scene.append(g); setTimeout(() => g.remove(), 600);
    }

    // A step is two poses: legs apart (feet planted, body low), then legs passing (body lifted 6 px).
    // Speed matches the stride so the feet do not skate.
    const STEP_FPS = 5;
    const front = [['f2', 0], ['f1', 6], ['f3', 0], ['f1', 6]];
    const side = [['s1-g', 0], ['h1-g', 6], ['s3-g', 0], ['h1-g', 6]];
    const walk = (cycle, t) => { const [f, lift] = cycle[Math.floor(t * STEP_FPS) % cycle.length]; st.frame = f; st.y = lift; };
    const SPEED = 230, SIDE_SPEED = 260;
    const jumpFrom = carX - 80, seatX = carX + CAR.seat, seatY = 18;

    const phases = [
      // walk out from behind the menu
      { d: (waveX + 140) / SPEED, run(t, d) { walk(front, t); st.x = lerp(-140, waveX, t / d); } },
      // wave hello
      { d: 1.5, start() { bubble.hidden = false; bubble.style.left = (waveX + 40) + 'px'; bubble.style.bottom = (G + 320) + 'px'; },
        run(t) { st.frame = 'wave'; st.y = 0; st.rot = Math.sin(t * 14) * 3; }, end() { bubble.hidden = true; st.rot = 0; } },
      // shades drop on
      { d: 1.2, start() { shades.hidden = false; },
        run(t) {
          const p = Math.min(1, t / 0.55), drop = (1 - ease(p)) * 140;
          shades.style.left = (st.x - 64 + 46) + 'px'; shades.style.bottom = (G + 300 - 31 - 16 + drop) + 'px';
          if (t >= 0.55 && st.frame !== 'wave-g') { st.frame = 'wave-g'; shades.hidden = true; glint(); }
        } },
      // stroll to the car
      { d: Math.max(0.8, (jumpFrom - waveX) / SIDE_SPEED), run(t, d) { walk(side, t); st.x = lerp(waveX, jumpFrom, t / d); } },
      // crouch...
      { d: 0.35, run(t) { st.frame = t < 0.12 ? 'h1-g' : 'h2-g'; st.y = 0; } },
      // ...and hop in
      { d: 0.7, run(t, d) {
          const p = t / d; st.frame = 'h3-g';
          st.x = lerp(jumpFrom, seatX, ease(p)); st.y = lerp(0, seatY, p) + Math.sin(p * Math.PI) * 230;
          st.clip = p > 0.5 ? Math.max(0, CAR.door - st.y) : 0;
        } },
      // the car settles under him
      { d: 0.45, run(t) { st.carY = -Math.sin(Math.min(1, t / 0.3) * Math.PI) * 6; st.y = seatY + st.carY; doorClip(); } },
      // look at you (and wave)
      { d: 1.5, start() { st.x = seatX + 30; st.y = seatY - 23; },
        run(t) { st.frame = 'wave-g'; st.rot = Math.sin(t * 12) * 2; doorClip(); if (t > 0.2 && !this.g) { this.g = 1; glint(); } } },
      // rev the engine
      { d: 0.7, run(t) { st.rot = 0; st.carY = Math.sin(t * 60) * 1.5; st.y = seatY - 23 + st.carY; doorClip(); if (Math.random() < 0.25) puff(false); } },
      // drive off
      { d: 3, start() { this.x0 = st.carX; this.m0 = st.x; },
        run(t) {
          const dx = 260 * t * t * t + 120 * t;                       // slow start, then away
          st.carX = this.x0 + dx * 2.2; st.x = this.m0 + dx * 2.2;
          st.carY = Math.abs(Math.sin(t * 18)) * 2; st.y = seatY - 23 + st.carY; doorClip();
          st.spin = (dx * 2.2) / CAR.R * (180 / Math.PI);
          if (Math.random() < 0.7) puff(true);
          if (st.carX > W + 200) this.done = true;
        } },
    ];

    let i = 0, t0 = null, raf;
    function tick(now) {
      if (t0 === null) { t0 = now; if (phases[0].start) phases[0].start(); }
      const ph = phases[i], t = (now - t0) / 1000;
      ph.run(Math.min(t, ph.d), ph.d); draw();
      if (t >= ph.d || ph.done) {
        if (ph.end) ph.end();
        i++; t0 = now;
        if (i >= phases.length) { stop(); return; }
        if (phases[i].start) phases[i].start();
      }
      raf = requestAnimationFrame(tick);
    }
    function stop() { cancelAnimationFrame(raf); stage.replaceChildren(); running = null; document.removeEventListener('keydown', onKey); }
    function onKey(e) { if (e.key === 'Escape') stop(); }
    document.addEventListener('keydown', onKey);
    draw(); raf = requestAnimationFrame(tick);
    running = { stop };
  }

  document.querySelectorAll('.egg-t').forEach((b) => b.addEventListener('click', play));
})();
