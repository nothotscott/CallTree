import tailwindcss from '@tailwindcss/vite';
import adapter from '@sveltejs/adapter-static';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
	plugins: [
		tailwindcss(),
		sveltekit({
			compilerOptions: {
				// Force runes mode for the project, except for libraries. Can be removed in svelte 6.
				runes: ({ filename }) =>
					filename.split(/[/\\]/).includes('node_modules') ? undefined : true
			},

			// Single-page-app mode: every route is served the same fallback document and routing
			// happens in the browser. The output is plain static files, which the ASP.NET host serves
			// from wwwroot - one container, one port, same origin as the API, so no CORS anywhere.
			// See deploy/README.md. `ssr = false` in src/routes/+layout.ts is what makes this legal.
			adapter: adapter({
				pages: 'build',
				assets: 'build',
				fallback: 'index.html',
				precompress: false
			})
		})
	],

	server: {
		proxy: {
			// Keeps the API same-origin in development, so there is no CORS to configure and the
			// browser sees the same URLs it would in a same-origin deployment. 5146 is the API's
			// http launch profile (CallTree.Api/Properties/launchSettings.json).
			'/api': {
				target: 'http://localhost:5146',
				changeOrigin: true
			}
		}
	}
});
