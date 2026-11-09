// Switchboard Remote Manager: loaded ahead of aws-azure-login (node --require / NODE_OPTIONS).
//
// 1. Catching the sign-in. In window mode aws-azure-login starts Chromium already loading the Microsoft sign-in page
//    (--app=<url>), and only then turns on the request interception that catches Microsoft's hand-off to AWS. When
//    the Microsoft session is remembered, sign-in completes silently within a second, often before that interception
//    is on: nothing catches it and the window carries on to the AWS console. So here the window starts on a small
//    local placeholder page instead, aws-azure-login watches that window and turns its interception on, and only
//    then (when it starts waiting for the page to load) does the window go to the real sign-in page.
// 2. The account. Entra's login_hint (SWITCHBOARD_LOGIN_HINT, the profile's azure_default_username) is added to the
//    Microsoft sign-in address in both window mode and background sign-in (page.goto), so Entra signs in that
//    account instead of silently using the one signed in to Windows.
// If anything here doesn't match what it expects, it lets aws-azure-login carry on as it would on its own.
'use strict';
(function () {
  const hint = process.env.SWITCHBOARD_LOGIN_HINT || '';
  const Module = require('module');
  const done = new WeakSet();
  const pending = new WeakMap();   // browser -> { url, started } for window mode: where the window should go
  const say = msg => { try { process.stderr.write('[switchboard] ' + msg + '\n'); } catch (e) { } };
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const hostOf = url => { try { return new URL(String(url)).hostname.toLowerCase(); } catch (e) { return ''; } };

  const MARKER = 'switchboard-sign-in';
  const PLACEHOLDER = 'data:text/html;charset=utf-8,' + encodeURIComponent(
    '<!doctype html><title>Sign in</title><!--' + MARKER + '-->' +
    '<body style="font:14px Segoe UI,sans-serif;color:#555;display:flex;align-items:center;justify-content:center;height:90vh">' +
    'Opening the Microsoft sign-in page ...</body>');
  const isPlaceholder = pg => { try { return String(pg.url()).includes(MARKER); } catch (e) { return false; } };

  function withHint(url) {
    if (!hint) return url;
    try {
      const text = String(url);
      const host = hostOf(text);
      if (!/(^|\.)login\.microsoftonline\.(com|us)$/.test(host)) return url;
      if (/[?&]login_hint=/i.test(text)) return url;
      // append rather than rebuild the URL, so the SAMLRequest parameter stays byte-for-byte as it was
      say('login_hint=' + hint + ' added to ' + host);
      return text + (text.includes('?') ? '&' : '?') + 'login_hint=' + encodeURIComponent(hint);
    } catch (e) {
      return url;
    }
  }

  // window mode: send the placeholder window to the real sign-in page, once
  function startSignIn(state, goto, why) {
    if (!state || state.started) return;
    state.started = true;
    say('opening the sign-in page (' + why + ')');
    goto(state.url).catch(() => { /* aws-azure-login reports what happens next */ });
  }

  function patchPage(page, state) {
    if (!page || done.has(page) || typeof page.goto !== 'function') return page;
    done.add(page);
    const goto = page.goto.bind(page);
    page.goto = (url, options) => {
      if (state && !state.started && isPlaceholder(page)) { state.started = true; }
      return goto(withHint(url), options);
    };
    if (state && typeof page.waitForNavigation === 'function') {
      // aws-azure-login turns its interception on first, then waits for the page to load: start the load here
      const waitForNavigation = page.waitForNavigation.bind(page);
      page.waitForNavigation = (...args) => {
        const waiting = waitForNavigation(...args);
        setImmediate(() => startSignIn(state, goto, 'interception is on'));
        return waiting;
      };
      // safety net: never leave the window on the placeholder
      setTimeout(() => startSignIn(state, goto, 'aws-azure-login didn\'t start it'), 8000).unref();
    }
    return page;
  }

  function patchBrowser(browser) {
    if (!browser || done.has(browser)) return browser;
    done.add(browser);
    if (typeof browser.newPage === 'function') {
      const newPage = browser.newPage.bind(browser);
      browser.newPage = async (...args) => patchPage(await newPage(...args));
    }
    if (typeof browser.pages === 'function') {
      const pages = browser.pages.bind(browser);
      browser.pages = async (...args) => {
        let list = await pages(...args);
        const state = pending.get(browser);
        if (state && !state.started) {
          // window mode: wait for the placeholder window (a local page: it's there almost at once) and put it first,
          // which is the tab aws-azure-login watches
          const deadline = Date.now() + 30000;
          while (!list.some(isPlaceholder) && Date.now() < deadline) {
            await sleep(100);
            list = await pages(...args);
          }
          const window = list.find(isPlaceholder);
          if (window) {
            list = [window].concat(list.filter(pg => pg !== window));
            return list.map(pg => patchPage(pg, pg === window ? state : null));
          }
          say('the sign-in window didn\'t appear; opening the sign-in page in the first tab');
          if (list[0]) startSignIn(state, list[0].goto.bind(list[0]), 'no placeholder window');
        }
        return list.map(pg => patchPage(pg));
      };
    }
    return browser;
  }

  function patchPuppeteer(p) {
    if (!p || done.has(p) || typeof p.launch !== 'function') return;
    done.add(p);
    const launch = p.launch.bind(p);
    p.launch = async (options, ...rest) => {
      let state = null;
      try {
        if (options && Array.isArray(options.args)) {
          options.args = options.args.map(a => {
            if (typeof a !== 'string' || !a.startsWith('--app=')) return a;
            state = { url: withHint(a.slice('--app='.length)), started: false };
            return '--app=' + PLACEHOLDER;
          });
        }
      } catch (e) { state = null; }
      const browser = await launch(options, ...rest);
      if (state && browser) pending.set(browser, state);
      return patchBrowser(browser);
    };
  }

  const load = Module._load;
  Module._load = function (request) {
    const exported = load.apply(this, arguments);
    if (request === 'puppeteer' || request === 'puppeteer-core') {
      // aws-azure-login may use either the module itself or its "default" export
      patchPuppeteer(exported);
      if (exported) patchPuppeteer(exported.default);
    }
    return exported;
  };
})();
