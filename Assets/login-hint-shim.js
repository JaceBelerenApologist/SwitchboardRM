// Switchboard Remote Manager: loaded ahead of aws-azure-login (node --require / NODE_OPTIONS).
// Adds Entra's login_hint to the Microsoft sign-in page aws-azure-login opens, so Entra signs in the profile's
// account (azure_default_username) instead of silently using the account signed in to Windows.
// It only adds a URL parameter; if anything here doesn't match, sign-in carries on exactly as before.
'use strict';
(function () {
  const hint = process.env.SWITCHBOARD_LOGIN_HINT;
  if (!hint) return;

  const Module = require('module');
  const done = new WeakSet();
  const say = msg => { try { process.stderr.write('[switchboard] ' + msg + '\n'); } catch (e) { } };

  function withHint(url) {
    try {
      const text = String(url);
      const host = new URL(text).hostname.toLowerCase();
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
      browser.pages = async (...args) => (await pages(...args)).map(patchPage);
    }
    return browser;
  }

  function patchPuppeteer(p) {
    if (!p || done.has(p) || typeof p.launch !== 'function') return;
    done.add(p);
    const launch = p.launch.bind(p);
    p.launch = async (...args) => patchBrowser(await launch(...args));
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
