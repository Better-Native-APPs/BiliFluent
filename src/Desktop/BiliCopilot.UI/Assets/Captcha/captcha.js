// Hosts one Passport V3, V4, or image challenge for the native sign-in window.
// Host -> page: { type: "init", captchaType, gt, challenge, imageData?, lang, theme, startText }
// Page -> host: { type: "loaded" } | { type: "ready" } | { type: "error", code }
//               | { type: "success", challenge, validate, seccode }
//               | { type: "success_v4", lot_number, captcha_output, pass_token, gen_time }
//               | { type: "success_img", img_code }
(function () {
    'use strict';

    var bridge = window.chrome && window.chrome.webview;
    if (!bridge) {
        return;
    }

    var SDK_URL = 'https://static.geetest.com/static/js/gt.0.5.0.js';
    var SDK_V4_URL = 'https://static.geetest.com/v4/gt4.js';
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

    function run(config, captchaType) {
        var lang = config.lang === 'en' ? 'en' : 'zh-cn';
        document.documentElement.lang = lang === 'en' ? 'en' : 'zh-CN';
        document.documentElement.dataset.theme = config.theme === 'dark' ? 'dark' : 'light';
        startButton.textContent = isText(config.startText, 64) ? config.startText : '';

        if (captchaType === 'img') {
            showImage(config, lang);
            return;
        }

        var script = document.createElement('script');
        script.src = captchaType === 'geetest4' ? SDK_V4_URL : SDK_URL;
        script.onerror = function () {
            fail('sdk_unavailable');
        };
        script.onload = function () {
            var initialize = captchaType === 'geetest4' ? window.initGeetest4 : window.initGeetest;
            if (typeof initialize !== 'function') {
                fail('sdk_invalid');
                return;
            }

            try {
                var options = captchaType === 'geetest4' ? {
                    captchaId: config.gt,
                    language: lang === 'en' ? 'eng' : 'zho',
                    product: 'bind',
                    https: true,
                    onError: function (error) { fail(error && error.code); }
                } : {
                    gt: config.gt,
                    challenge: config.challenge,
                    offline: false,
                    new_captcha: true,
                    product: 'bind',
                    https: true,
                    lang: lang,
                    timeout: 15000
                };
                initialize(options, function (captcha) { attach(captcha, captchaType); });
            } catch (error) {
                fail('sdk_init');
            }
        };
        document.head.appendChild(script);
    }

    function attach(captcha, captchaType) {
        function show() {
            if (captchaType === 'geetest4') {
                captcha.showCaptcha();
            } else {
                captcha.verify();
            }
        }

        captcha.onReady(function () {
            if (finished) {
                return;
            }

            post({ type: 'ready' });
            show();
        });
        captcha.onSuccess(function () {
            var result = captcha.getValidate();
            if (!result || finished) {
                return;
            }

            if (captchaType === 'geetest4') {
                if (!isText(result.lot_number, 256) || !isText(result.captcha_output, 8192)
                    || !isText(result.pass_token, 256) || !isText(String(result.gen_time || ''), 256)) {
                    fail('sdk_proof_missing');
                    return;
                }

                finished = true;
                startButton.hidden = true;
                post({
                    type: 'success_v4',
                    lot_number: result.lot_number,
                    captcha_output: result.captcha_output,
                    pass_token: result.pass_token,
                    gen_time: String(result.gen_time)
                });
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
            fail(error && (error.error_code || error.code));
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
                show();
            }
        });
    }

    function showImage(config, lang) {
        var form = document.getElementById('image-form');
        var image = document.getElementById('captcha-image');
        var input = document.getElementById('image-code');
        var label = document.getElementById('image-label');
        var submit = document.getElementById('image-submit');
        var instruction = lang === 'en' ? 'Enter the 5 characters in the image' : '请输入图片中的 5 位验证码';
        label.textContent = instruction;
        image.alt = instruction;
        submit.textContent = lang === 'en' ? 'Verify' : '验证';
        image.onload = function () {
            if (!finished) {
                form.hidden = false;
                post({ type: 'ready' });
                input.focus();
            }
        };
        image.onerror = function () { fail('image_unavailable'); };
        form.addEventListener('submit', function (event) {
            event.preventDefault();
            if (finished || !/^[!-~]{5}$/.test(input.value)) {
                return;
            }

            finished = true;
            post({ type: 'success_img', img_code: input.value });
            input.value = '';
        });
        // The host downloaded this image in memory; the Passport token never enters this page.
        image.src = config.imageData;
    }

    bridge.addEventListener('message', function (event) {
        var data = event.data;
        if (started || !data || data.type !== 'init') {
            return;
        }

        var captchaType = data.captchaType || 'geetest';
        var valid = captchaType === 'img'
            ? isText(data.imageData, 1400000) && /^data:image\/(png|jpeg|gif|webp);base64,/.test(data.imageData)
            : isText(data.gt, 128) && (captchaType === 'geetest4' || captchaType === 'geetest' && isText(data.challenge, 128));
        if (!valid) {
            return;
        }

        started = true;
        run(data, captchaType);
    });

    post({ type: 'loaded' });
})();
