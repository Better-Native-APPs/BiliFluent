---
# https://vitepress.dev/reference/default-theme-home-page
layout: home

hero:
  name: BiliFluent
  text: 为 Windows 优化的哔哩哔哩第三方桌面客户端
  tagline: Fluent UI 原生体验 · 无广告 · 专注低资源占用与流畅播放
  actions:
    - theme: brand
      text: 快速安装
      link: /install
    - theme: alt
      text: 播放体验
      link: /player
    - theme: alt
      text: 关于项目
      link: /about
    - theme: alt
      text: GitHub
      link: https://github.com/Better-Native-APPs/BiliFluent
  image:
    src: /logo-big.png
    alt: BiliFluent

features:
  - icon: 🚀
    title: Windows 原生体验
    details: 采用 Fluent UI 与 WinUI 3 原生控件，支持键盘快捷键与触控手势。
  - icon: 🍃
    title: 省电与低资源占用
    details: 默认 mpv 内嵌省电弹幕，合并刷新播放进度，最小化时自动调度效率模式。
  - icon: 📺
    title: 低配硬件预设
    details: 默认 1080P60 画质上限与 AVC/H.264 优先，默认 64 MiB 缓存，支持「快速」配置切换。
  - icon: 🛡️
    title: 无广告与独立共存
    details: 开源无广告，独立包身份不覆盖官方客户端或上游应用，内置空降助手。
---

BiliFluent 基于 Richasy 的「哔哩助理」([Bili Copilot](https://github.com/Richasy/Bili.Copilot)) 二次开发，遵循 GPL-3.0 许可协议。

<style>
:root {
  --vp-home-hero-name-color: transparent;
  --vp-home-hero-name-background: -webkit-linear-gradient(-12deg, #DFC3FC 20%, #FD99D0 60%, #FE508E);

  --vp-home-hero-image-background-image: linear-gradient(160deg, #F3F3F3 50%, #FF79B2 60%, #FEB5FE 80%);
  --vp-home-hero-image-filter: blur(44px);
}

@media (min-width: 640px) {
  :root {
    --vp-home-hero-image-filter: blur(56px);
  }
}

@media (min-width: 960px) {
  :root {
    --vp-home-hero-image-filter: blur(68px);
  }
}
</style>
