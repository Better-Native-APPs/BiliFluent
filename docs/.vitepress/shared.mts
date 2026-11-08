import { defineConfig } from 'vitepress'

export const shared = defineConfig({
    title: 'BiliFluent',
    description: '采用 Fluent UI / WinUI 3、专为 Windows 优化的无广告第三方哔哩哔哩桌面客户端',
  
    lastUpdated: true,
    cleanUrls: true,
    metaChunk: true,

    markdown: {
        codeTransformers: [
          {
            postprocess(code) {
              return code.replace(/\[\!\!code/g, '[!code')
            }
          }
        ]
      },

    head: [
      ['link', { rel: 'icon', type: 'image/svg+xml', href: '/logo-small.svg' }],
      ['link', { rel: 'icon', type: 'image/png', href: '/logo-small.png' }],
      ['meta', { name: 'theme-color', content: '#FF578D' }],
      ['meta', { property: 'og:type', content: 'website' }],
      ['meta', { property: 'og:locale', content: 'zh' }],
      ['meta', { property: 'og:title', content: 'BiliFluent | Fluent UI / WinUI 3 哔哩哔哩 Windows 客户端' }],
      ['meta', { property: 'og:site_name', content: 'BiliFluent' }],
      ['meta', { property: 'og:url', content: 'https://github.com/Better-Native-APPs/BiliFluent' }],
    ],

    themeConfig: {
        logo: { src: '/logo-small.svg', width: 24, height: 24 },
    
        socialLinks: [
          { icon: 'github', link: 'https://github.com/Better-Native-APPs/BiliFluent' }
        ],
      }
})  
