#!/usr/bin/env node
'use strict';

const pkg = require('../package.json');
const repo = pkg.repository && pkg.repository.url
  ? pkg.repository.url.replace(/^git\+/, '').replace(/\.git$/, '')
  : '';
const owner = pkg.author || 'JacobTheJacobs';
const name  = pkg.name || 'ReClaw';

const banner = `
    .-._                          _,
    \`--'\`-._                  _,-'-.
           \`-._           _,-'      \`-.
               \`-._   _,-'            \`-.
                   \`-'                   \`.
              ___                          \`.
           ,-'   \`-.                        \`.
          /         \\.                       \`.
         /    _      \\         ___             \`.
        |    ( )      |      ,'   \`.             |
        |     \`-'     |     /  ,--. \\            |
         \\            /    /  /    \\ \\           /
          \`.        ,'    /  /  ()  \\ \\         /
            \`------'    |  |        | |        /
                        |  |   __   | |       /
                         \\ \\ ,'  \`. / /      /
                          \`-'      \`-'      /
                            \\              /
                  ~~~~~~~~~~~\\~~~~~~~~~~~~/~~~~~~~~~~~

  ${name}  |  by ${owner}
  ${repo}
  OpenClaw backup, restore & recovery tool.
`;

process.stdout.write(banner + '\n');
