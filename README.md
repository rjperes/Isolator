# Isolator
Isolator is a code isolation (sandbox) framework for .NET.
See https://developmentwithadot.blogspot.com/2025/10/introducing-isolator-framework-for.html.

## Strategies
The following strategies are implemented:
* Process isolation: a process is used for executing the plugin
* Assembly isolation: a new assembly is used for executing the plugin and then unloaded
* Distributed isolation: the plugin is executed on a possibly remote machine via TCP/IP connection
* Docker: a Docker image is spawn for running the plugin and is then destroyed

[![Nuget downloads](https://img.shields.io/nuget/v/isolator.svg)](https://www.nuget.org/packages/Isolator/)
[![Nuget](https://img.shields.io/nuget/dt/isolator)](https://www.nuget.org/packages/Isolator/)
[![License: Apache 2](https://img.shields.io/badge/License-Apache2-yellow.svg)](https://github.com/rjperes/Isolator/blob/master/LICENSE)
[![Build](https://github.com/rjperes/Isolator/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/rjperes/Isolator/actions/workflows/build-and-test.yml)
