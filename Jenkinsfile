pipeline {
    agent any

    environment {
        COMPOSE_PROJECT_DIR = 'Realtime-D3-signalR-dotnet-postgresql'
    }

    stages {
        stage('拉取代码') {
            steps {
                echo "--------------- 拉取 Git 仓库代码 ---------------"
                checkout scm
            }
        }

        stage('获取 Git 提交哈希') {
            steps {
                script {
                    dir(env.COMPOSE_PROJECT_DIR) {
                        GITHASH = sh(
                            script: 'git rev-parse --short HEAD',
                            returnStdout: true
                        ).trim()
                        echo "Git 哈希: ${GITHASH}"
                    }
                }
            }
        }

        stage('使用 docker-compose 启动服务') {
            steps {
                echo "--------------- 使用 docker-compose 部署服务 ---------------"
                dir(env.COMPOSE_PROJECT_DIR) {
                    sh 'docker-compose down || true'  // 关闭旧容器
                    sh 'docker-compose pull'          // 拉取最新镜像（如果使用远程镜像）
                    sh 'docker-compose build'         // 构建所有服务
                    sh 'docker-compose up -d'         // 后台启动所有服务
                }
            }
        }

        stage('清理旧镜像') {
            steps {
                echo "--------------- 清理未使用的 Docker 镜像 ---------------"
                sh 'docker image prune -f'
            }
        }
    }

    post {
        always {
            echo "--------------- 显示当前 Docker 镜像状态 ---------------"
            sh 'docker images'
            echo "--------------- 显示当前运行的容器 ---------------"
            sh 'docker ps -a'
        }
    }
}
