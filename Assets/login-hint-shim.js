// Switchboard Remote Manager: loaded ahead of aws-azure-login (node --require / NODE_OPTIONS).
//
// 1. The sign-in window: in window mode aws-azure-login starts Chromium with the sign-in page (--app=<url>), waits a
//    fixed 200 ms and then watches whichever tab Chromium lists first for the moment Microsoft hands the sign-in to
//    AWS. When Chromium starts slowly (a new or changed sign-in profile), that's the wrong tab: the hand-off isn't
//    caught and the window opens the AWS console instead. Here browser.pages() waits for the sign-in window and lists
//    it first, so aws-azure-login always watches the right one.
// 2. The account: adds Entra's login_hint (SWITCHBOARD_LOGIN_HINT, the profile's azure_default_username) to the
//    Microsoft sign-in address, both the --app=<url> of window mode and page.goto() of background sign-in, so Entra
//    signs in that account instead of silently using the one signed in to Windows.
// If anything here doesn't match what it expects, sign-in carries on exactly as aws-azure-login does it.
'use strict';
(function () {
  const hint = process.env.SWITCHBOARD_LOGIN_HINT || '';
  const Module = require('module');
  const done = new WeakSet();
  const appUrls = new WeakMap();   // browser -> the --app=<url> it was started with (window mode only)
  const say = msg => { try { process.stderr.write('[switchboard] ' + msg + '\n'); } catch (e) { } };
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const hostOf = url => { try { return new URL(String(url)).hostname.toLowerCase(); } catch (e) { return ''; } };

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

  function patchPage(page) {
    if (!page || done.has(page) || typeof page.goto !== 'function') return page;
    done.add(page);
    const goto = page.goto.bind(page);
    page.goto = (url, options) => goto(withHint(url), options);
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
        const appUrl = appUrls.get(browser);
        if (appUrl) {
          // window mode: wait for the sign-in window (Microsoft's page, or AWS if it went straight through) ...
          const appHost = hostOf(appUrl);
          const isSignIn = pg => {
            let h = '';
            try { h = hostOf(pg.url()); } catch (e) { }
            return h !== '' && (h === appHost || /(^|\.)(microsoftonline\.(com|us)|amazon\.com|amazonaws\.(cn|com)|amazonaws-us-gov\.com)$/.test(h));
          };
          const deadline = Date.now() + 30000;
          while (!list.some(isSignIn) && Date.now() < deadline) {
            await sleep(250);
            list = await pages(...args);
          }
          // ... and put it first, which is the tab aws-azure-login watches
          const signIn = list.filter(isSignIn);
          if (signIn.length > 0) {
            if (list[0] !== signIn[0]) say('the sign-in window wasn\'t the first tab; watching it instead (' + list.length + ' tabs)');
            list = signIn.concat(list.filter(pg => !isSignIn(pg)));
          } else {
            say('the sign-in window didn\'t appear within 30 seconds; leaving the tabs as they are');
          }
        }
        return list.map(patchPage);
      };
    }
    return browser;
  }

  function patchPuppeteer(p) {
    if (!p || done.has(p) || typeof p.launch !== 'function') return;
    done.add(p);
    const launch = p.launch.bind(p);
    p.launch = async (options, ...rest) => {
      let appUrl = null;
      try {
        if (options && Array.isArray(options.args)) {
          options.args = options.args.map(a => {
            if (typeof a !== 'string' || !a.startsWith('--app=')) return a;
            appUrl = withHint(a.slice('--app='.length));
            return '--app=' + appUrl;
          });
        }
      } catch (e) { appUrl = null; }
      const browser = await launch(options, ...rest);
      if (appUrl && browser) appUrls.set(browser, appUrl);
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
