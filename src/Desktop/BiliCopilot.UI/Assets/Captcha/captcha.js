// Hosts one GeeTest v3 challenge for the native sign-in window.
// Host -> page: { type: "init", gt, challenge, lang, theme, startText }
// Page -> host: { type: "loaded" } | { type: "ready" } | { type: "error", code }
//               | { type: "success", challenge, validate, seccode }
(function () {
    'use strict';

    var bridge = window.chrome && window.chrome.webview;
    if (!bridge) {
        return;
    }

    var SDK_URL = 'https://static.geetest.com/static/js/gt.0.5.0.js';
    var startButton = document.getElementById('start');
    var started = false;
    var finished = false;

    function post(message) {
        bridge.postMessage(message);
    }

    function fail(code) {
        if (finished) {
            return;
        }

        finished = true;
        startButton.hidden = true;
        post({ type: 'error', code: String(code || 'unknown').slice(0, 32) });
    }

    function isText(value, limit) {
        return typeof value === 'string' && value.length > 0 && value.length <= limit;
    }

    function run(config) {
        var lang = config.lang === 'en' ? 'en' : 'zh-cn';
        document.documentElement.lang = lang === 'en' ? 'en' : 'zh-CN';
        document.documentElement.dataset.theme = config.theme === 'dark' ? 'dark' : 'light';
        startButton.textContent = isText(config.startText, 64) ? config.startText : '';

        var script = document.createElement('script');
        script.src = SDK_URL;
        script.onerror = function () {
            fail('sdk_unavailable');
        };
        script.onload = function () {
            if (typeof window.initGeetest !== 'function') {
                fail('sdk_invalid');
                return;
            }

            try {
                window.initGeetest({
                    gt: config.gt,
                    challenge: config.challenge,
                    offline: false,
                    new_captcha: true,
                    product: 'bind',
                    https: true,
                    lang: lang,
                    timeout: 15000
                }, attach);
            } catch (error) {
                fail('sdk_init');
            }
        };
        document.head.appendChild(script);
    }

    function attach(captcha) {
        captcha.onReady(function () {
            if (finished) {
                return;
            }

            post({ type: 'ready' });
            captcha.verify();
        });
        captcha.onSuccess(function () {
            var result = captcha.getValidate();
            if (!result || finished) {
                return;
            }

            finished = true;
            startButton.hidden = true;
            post({
                type: 'success',
                challenge: String(result.geetest_challenge),
                validate: String(result.geetest_validate),
                seccode: String(result.geetest_seccode)
            });
        });
        captcha.onError(function (error) {
            fail(error && error.error_code);
        });
        captcha.onClose(function () {
            // The challenge panel has its own close button; offer a way back in.
            if (!finished) {
                startButton.hidden = false;
                startButton.focus();
            }
        });
        startButton.addEventListener('click', function () {
            if (!finished) {
                startButton.hidden = true;
                captcha.verify();
            }
        });
    }

    bridge.addEventListener('message', function (event) {
        var data = event.data;
        if (started || !data || data.type !== 'init' || !isText(data.gt, 128) || !isText(data.challenge, 128)) {
            return;
        }

        started = true;
        run(data);
    });

    post({ type: 'loaded' });
})();
